#nullable enable

using System;

namespace OpWeave.UnityAdapter
{
    /// <summary>Which candidate form a <c>unity.package.add</c> <c>id</c> value parsed as (SPEC §G8.1).</summary>
    public enum PackageIdKind
    {
        /// <summary><c>name@version</c> (or a bare name) resolved against the registry.</summary>
        Registry,

        /// <summary>A Git URL in <c>https://</c>, <c>ssh://</c> or SCP (<c>git@host:path</c>) notation.</summary>
        Git,
    }

    /// <summary>
    /// The parsed source half of a constant <c>unity.package.add</c> <c>id</c>
    /// (SPEC §G8.1, §0.6 SG-19): either a registry package name plus optional
    /// version, or a Git origin normalized for the §0.6 exact-match rule, with
    /// the revision (<c>#…</c>) and subPath (<c>?path=…</c>) split out — they are
    /// kept for Plan/execution and never take part in origin matching.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Normalization is exactly what §G8.1 allows: scheme and host are ASCII
    /// lowercased; path case, a trailing <c>.git</c>, an explicit port and the
    /// notation itself (<c>https://</c> vs <c>ssh://</c> vs SCP) stay literal,
    /// so other spellings of the "same" repository count as a different origin
    /// and can only match an entry written in the same notation. A
    /// <c>git+</c> scheme prefix is likewise a different scheme, not an alias.
    /// </para>
    /// <para>
    /// Documented local readings where SPEC leaves detail open (all fail closed
    /// and reversible): an unparseable or unsafe-shaped <c>id</c> is simply "not
    /// an allowed origin" for the caller (OW-PERM-001); a bare package name
    /// without <c>@version</c> parses as a registry candidate whose allowance is
    /// decided on the name alone; a revision or subPath attached to a registry
    /// form makes the id unrecognizable rather than silently ignored. The Git
    /// arms run the same SG-19 user, host, port and repo-path grammar as the
    /// allowlist rule — only scheme and host case is normalized — so a form
    /// the closed grammar cannot express is unrecognizable rather than merely
    /// unmatched. Both sides also share one form decision
    /// (<see cref="PackageSourceRule.ClassifyGitSource"/>): a URL only when the
    /// text starts with '<c>&lt;scheme&gt;://</c>', otherwise the SCP boundary
    /// — the first ':' that no '/' precedes — so a '://' run inside an SCP
    /// repository path stays path text on both sides.
    /// </para>
    /// </remarks>
    public sealed class PackageIdSource
    {
        private PackageIdSource(
            PackageIdKind kind,
            string? packageName,
            string? version,
            string? normalizedOrigin,
            string? revision,
            string? subPath)
        {
            Kind = kind;
            PackageName = packageName;
            Version = version;
            NormalizedOrigin = normalizedOrigin;
            Revision = revision;
            SubPath = subPath;
        }

        /// <summary>Registry or Git.</summary>
        public PackageIdKind Kind { get; }

        /// <summary>The package name when <see cref="Kind"/> is <see cref="PackageIdKind.Registry"/>.</summary>
        public string? PackageName { get; }

        /// <summary>The <c>@version</c> text, or null for a bare registry name.</summary>
        public string? Version { get; }

        /// <summary>
        /// The normalized Git origin (<c>scheme://[git@]host[:port]/path</c> or SCP
        /// <c>git@host:path</c>) when <see cref="Kind"/> is <see cref="PackageIdKind.Git"/>.
        /// </summary>
        public string? NormalizedOrigin { get; }

        /// <summary>The <c>#…</c> revision text, kept separately from the origin; null when absent.</summary>
        public string? Revision { get; }

        /// <summary>The <c>?path=…</c> subPath value, kept separately from the origin; null when absent.</summary>
        public string? SubPath { get; }

        /// <summary>
        /// The §G8.1 credential detection on the raw <c>id</c> text: userinfo on
        /// any non-SSH URL (<c>user@</c>, <c>user:password@</c>, <c>token@</c>), a
        /// password part in any userinfo (including SSH and SCP-shaped text), and
        /// query parameters other than UPM's <c>path</c> (<c>?access_token=…</c>).
        /// Unrecognizable shapes that merely look credential-bearing also report
        /// <c>true</c>, per §G8.1's fail-closed rule.
        /// </summary>
        /// <remarks>
        /// The check decides the candidate form the same way the parser does
        /// (<see cref="PackageSourceRule.ClassifyGitSource"/>): an authority —
        /// and therefore URL userinfo — exists only when the text starts with
        /// '<c>&lt;scheme&gt;://</c>'. Inside an SCP-form candidate the
        /// '<c>://</c>' and '<c>@</c>' characters of the repository path are
        /// ordinary path text and are never read as a scheme separator or a
        /// userinfo delimiter, while a '<c>:</c>' before the first '<c>@</c>'
        /// may still be a password part (<c>user:&lt;secret&gt;@host:…</c>) and
        /// stays fail-closed credentials (§G8.1 "判定できない形は安全側で拒否する").
        /// </remarks>
        public static bool ContainsCredentials(string? packageId)
        {
            if (packageId is null || packageId.Length == 0)
            {
                return false;
            }
            var text = packageId;
            var hash = text.IndexOf('#');
            if (hash >= 0)
            {
                text = text.Substring(0, hash);
            }
            var q = text.IndexOf('?');
            if (q >= 0)
            {
                var query = text.Substring(q + 1);
                text = text.Substring(0, q);
                foreach (var part in query.Split('&'))
                {
                    if (!part.StartsWith("path=", StringComparison.Ordinal))
                    {
                        // Anything but UPM's path parameter is a credential form.
                        return true;
                    }
                }
            }
            if (PackageSourceRule.ClassifyGitSource(text, out var split) ==
                GitSourceForm.Url)
            {
                var authority = text.Substring(split + "://".Length);
                var slash = authority.IndexOf('/');
                if (slash >= 0)
                {
                    authority = authority.Substring(0, slash);
                }
                var at = authority.LastIndexOf('@');
                if (at < 0)
                {
                    return false;
                }
                var userinfo = authority.Substring(0, at);
                if (userinfo.IndexOf(':') >= 0 || userinfo.IndexOf('@') >= 0)
                {
                    // A password part or an unrecognizable userinfo: credentials.
                    return true;
                }
                // A bare user name is credentials everywhere except the SSH form.
                return !IsScheme(text.Substring(0, split), "ssh");
            }
            var firstAt = text.IndexOf('@');
            return firstAt > 0 && text.Substring(0, firstAt).IndexOf(':') >= 0;
        }

        /// <summary>
        /// Parses a constant <c>id</c> into registry or Git form after splitting
        /// the revision and subPath away. Does not run
        /// <see cref="ContainsCredentials"/> — callers order that check first so a
        /// credential-bearing id is OW-OP-006, not a parse failure. Malformed,
        /// whitespace-bearing, non-ASCII or ambiguous ids return <c>false</c>.
        /// </summary>
        public static bool TryParse(string? packageId, out PackageIdSource? source)
        {
            source = null;
            if (packageId is null || packageId.Length == 0 || !PackageSourceRule.IsAsciiPrintable(packageId))
            {
                return false;
            }

            var text = packageId;
            string? revision = null;
            var hash = text.IndexOf('#');
            if (hash >= 0)
            {
                revision = text.Substring(hash + 1);
                text = text.Substring(0, hash);
                if (revision.Length == 0 || revision.IndexOf('#') >= 0)
                {
                    return false;
                }
            }
            string? subPath = null;
            var q = text.IndexOf('?');
            if (q >= 0)
            {
                var query = text.Substring(q + 1);
                text = text.Substring(0, q);
                if (!TryParseQuery(query, out subPath))
                {
                    return false;
                }
            }

            // The form is decided first and only once, by the same procedure the
            // allowlist rule applies: a URL only when the candidate starts with
            // '<scheme>://', else SCP '[user@]host:path' whose host/path boundary
            // is the first ':' that no '/' precedes — so a '://' run inside an
            // SCP repository path stays path text, not a scheme separator.
            var form = PackageSourceRule.ClassifyGitSource(text, out var split);
            if (form == GitSourceForm.Url)
            {
                return TryParseUrl(text, split, revision, subPath, out source);
            }
            if (form == GitSourceForm.Scp)
            {
                return TryParseScp(
                    text.Substring(0, split), text.Substring(split + 1),
                    revision, subPath, out source);
            }
            var at = text.IndexOf('@');
            if (at < 0)
            {
                return TryParseRegistry(text, null, revision, subPath, out source);
            }
            // A registry "name@version": a second '@' or any ':' inside the
            // version stays unrecognizable.
            var version = text.Substring(at + 1);
            return version.IndexOf('@') < 0 && version.IndexOf(':') < 0 &&
                TryParseRegistry(
                    text.Substring(0, at), version, revision, subPath, out source);
        }

        private static bool TryParseQuery(string query, out string? subPath)
        {
            subPath = null;
            if (query.Length == 0)
            {
                return false;
            }
            foreach (var part in query.Split('&'))
            {
                if (!part.StartsWith("path=", StringComparison.Ordinal))
                {
                    return false;
                }
                var value = part.Substring("path=".Length);
                if (subPath is not null || !PackageSourceRule.IsValidRepoPath(value))
                {
                    return false;
                }
                subPath = value;
            }
            return subPath is not null;
        }

        private static bool TryParseUrl(
            string text, int schemeSep, string? revision, string? subPath,
            out PackageIdSource? source)
        {
            source = null;
            var scheme = text.Substring(0, schemeSep);
            var isSsh = IsScheme(scheme, "ssh");
            if (!isSsh && !IsScheme(scheme, "https"))
            {
                return false;
            }
            var rest = text.Substring(schemeSep + 3);
            var slash = rest.IndexOf('/');
            if (slash <= 0)
            {
                return false;
            }
            var authority = rest.Substring(0, slash);
            var path = rest.Substring(slash); // includes the leading '/'
            var userinfo = "";
            var hostPort = authority;
            var at = authority.LastIndexOf('@');
            if (at >= 0)
            {
                userinfo = authority.Substring(0, at);
                hostPort = authority.Substring(at + 1);
                // The closed grammar knows only the fixed SSH "git@" user;
                // every other userinfo is unrecognizable here (credentials
                // are the caller's earlier §G8.1 check).
                if (!isSsh || !string.Equals(userinfo, "git", StringComparison.Ordinal))
                {
                    return false;
                }
            }
            else if (isSsh)
            {
                // ssh:// without the fixed git@ user is outside the grammar.
                return false;
            }
            var host = hostPort;
            var port = "";
            var colon = hostPort.LastIndexOf(':');
            if (colon >= 0)
            {
                host = hostPort.Substring(0, colon);
                port = hostPort.Substring(colon + 1);
                if (host.IndexOf(':') >= 0 || !PackageSourceRule.IsValidPort(port))
                {
                    return false;
                }
            }
            var normalizedHost = AsciiLower(host);
            if (!PackageSourceRule.IsValidGitHost(normalizedHost) ||
                path.Length < 2 ||
                !PackageSourceRule.IsValidRepoPath(path))
            {
                return false;
            }
            var normalized =
                AsciiLower(scheme) + "://" +
                (userinfo.Length > 0 ? userinfo + "@" : "") +
                normalizedHost +
                (port.Length > 0 ? ":" + port : "") +
                path;
            source = new PackageIdSource(
                PackageIdKind.Git, null, null, normalized, revision, subPath);
            return true;
        }

        private static bool TryParseScp(
            string authority, string path, string? revision, string? subPath,
            out PackageIdSource? source)
        {
            source = null;
            var user = "";
            var host = authority;
            var at = authority.IndexOf('@');
            if (at >= 0)
            {
                user = authority.Substring(0, at);
                host = authority.Substring(at + 1);
            }
            // The closed grammar fixes the SCP user to "git"; the host must
            // satisfy the Git-host grammar once lowercased (which also bars a
            // second '@' in the host part), while the repository path keeps
            // the full RFC 3986 pchar set — ':', '@' and even a '://' run
            // included.
            var normalizedHost = AsciiLower(host);
            if (!string.Equals(user, "git", StringComparison.Ordinal) ||
                !PackageSourceRule.IsValidGitHost(normalizedHost) ||
                !PackageSourceRule.IsValidRepoPath(path))
            {
                return false;
            }
            source = new PackageIdSource(
                PackageIdKind.Git, null, null,
                user + "@" + normalizedHost + ":" + path,
                revision, subPath);
            return true;
        }

        private static bool TryParseRegistry(
            string name, string? version, string? revision, string? subPath,
            out PackageIdSource? source)
        {
            source = null;
            // A registry id carrying Git baggage is an unrecognizable shape.
            if (revision is not null || subPath is not null ||
                !PackageSourceRule.IsValidRegistryName(name) ||
                (version is not null && version.Length == 0))
            {
                return false;
            }
            source = new PackageIdSource(
                PackageIdKind.Registry, name, version, null, null, null);
            return true;
        }

        private static bool IsScheme(string scheme, string expected)
        {
            if (scheme.Length != expected.Length)
            {
                return false;
            }
            for (var i = 0; i < scheme.Length; i++)
            {
                if (PackageSourceRule.AsciiLower(scheme[i]) != expected[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static string AsciiLower(string text)
        {
            var chars = text.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = PackageSourceRule.AsciiLower(chars[i]);
            }
            return new string(chars);
        }
    }
}
