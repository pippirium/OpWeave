#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.UnityAdapter
{
    /// <summary>The shape of one validated <c>allowedPackageSources</c> entry (SPEC §0.6 SG-19).</summary>
    public enum PackageSourceKind
    {
        /// <summary><c>registry:&lt;dot-separated-name&gt;</c> — one exact package name.</summary>
        RegistryName,

        /// <summary><c>registry:&lt;dot-separated-prefix&gt;.*</c> — a terminal <c>.*</c> wildcard.</summary>
        RegistryPrefix,

        /// <summary>
        /// One of the three Git entry notations (<c>git:https://</c>, <c>git:ssh://git@</c>,
        /// <c>git:git@</c>), matched against a normalized candidate origin by exact
        /// ordinal string equality.
        /// </summary>
        GitOrigin,
    }

    /// <summary>
    /// The shape <see cref="PackageSourceRule.ClassifyGitSource"/> assigns to a
    /// bare Git origin text, decided once and identically for SG-19 entries and
    /// <c>unity.package.add</c> candidate ids (SPEC §0.6 SG-19).
    /// </summary>
    internal enum GitSourceForm
    {
        /// <summary>The text starts with '<c>&lt;scheme&gt;://</c>'.</summary>
        Url,

        /// <summary>
        /// The text is '<c>[user@]host:path</c>' — a ':' appears before the first '/'.
        /// </summary>
        Scp,

        /// <summary>Neither a URL nor an SCP shape.</summary>
        Other,
    }

    /// <summary>
    /// One parsed and grammar-checked entry of the <c>allowedPackageSources</c>
    /// setting: the closed SG-19 grammar of SPEC §0.6. Instances exist only for
    /// entries that satisfy the grammar; the settings loader emits OW-CFG-002
    /// with the element's JsonPath for anything else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The grammar accepts exactly: <c>registry:&lt;dot-separated-name&gt;</c>,
    /// <c>registry:&lt;dot-separated-prefix&gt;.*</c> (the wildcard may only be
    /// the terminal segment), <c>git:https://&lt;lowercase-host&gt;/&lt;absolute-repo-path&gt;</c>,
    /// <c>git:ssh://git@&lt;lowercase-host&gt;/&lt;absolute-repo-path&gt;</c> and
    /// <c>git:git@&lt;lowercase-host&gt;:&lt;repo-path&gt;</c>. Registry segments
    /// are <c>[a-z0-9][a-z0-9_-]*</c>. A Git host must already be lowercase, may
    /// carry an explicit port (<c>:1-65535</c>) and must not be localhost, a
    /// loopback or a link-local address; repository paths allow the RFC 3986
    /// <c>pchar</c> set (unreserved, sub-delims, <c>:</c> and <c>@</c>) plus the
    /// <c>/</c> segment separator, and forbid whitespace, <c>..</c>, queries,
    /// fragments and ambiguous percent escapes (only escapes decoding to
    /// <c>[A-Za-z0-9_-~]</c> are unambiguous).
    /// </para>
    /// <para>
    /// Documented local readings where SPEC leaves detail open (all fail closed
    /// and reversible): bracketed IPv6 host literals are rejected rather than
    /// range-parsed for the loopback/link-local exclusions; hosts spelled as
    /// non-canonical IP literals (all-digit labels, <c>0x</c>-prefixed labels,
    /// leading-zero or short quads) are ambiguous and rejected; an explicit
    /// port of <c>0</c> and host names with a trailing dot are accepted or
    /// rejected only by the literal text — a trailing dot is kept as part of
    /// the origin notation, not normalized away. A <c>:</c> inside a repository
    /// path is a literal path character: URL forms carry it after the first
    /// <c>/</c>, and the SCP host/path boundary is the first <c>:</c> that no
    /// <c>/</c> precedes, matching Git's own scp-style parse. The entry grammar
    /// and the candidate id parser share that form decision
    /// (<see cref="ClassifyGitSource"/>): a URL only when the text starts with
    /// '<c>&lt;scheme&gt;://</c>', otherwise the SCP boundary — so a
    /// '<c>://</c>' run inside an SCP repository path stays path text on both
    /// sides.
    /// </para>
    /// </remarks>
    public sealed class PackageSourceRule
    {
        private PackageSourceRule(
            string entry,
            PackageSourceKind kind,
            string? registryName,
            string? registryPrefix,
            string? normalizedGitOrigin)
        {
            Entry = entry;
            Kind = kind;
            RegistryName = registryName;
            RegistryPrefix = registryPrefix;
            NormalizedGitOrigin = normalizedGitOrigin;
        }

        /// <summary>The entry text as written in the settings file.</summary>
        public string Entry { get; }

        /// <summary>Which grammar arm produced this rule.</summary>
        public PackageSourceKind Kind { get; }

        /// <summary>The exact package name when <see cref="Kind"/> is <see cref="PackageSourceKind.RegistryName"/>.</summary>
        public string? RegistryName { get; }

        /// <summary>
        /// The match prefix — including the final dot — when <see cref="Kind"/> is
        /// <see cref="PackageSourceKind.RegistryPrefix"/> (e.g. <c>"com.unity."</c> for
        /// <c>registry:com.unity.*</c>).
        /// </summary>
        public string? RegistryPrefix { get; }

        /// <summary>
        /// The normalized origin text — the entry text after <c>git:</c> — when
        /// <see cref="Kind"/> is <see cref="PackageSourceKind.GitOrigin"/>. The grammar
        /// already forced a lowercase host, so the stored text is the normalized form.
        /// </summary>
        public string? NormalizedGitOrigin { get; }

        /// <summary>
        /// Parses <paramref name="entry"/> against the closed SG-19 grammar. Returns
        /// <c>false</c> (and null <paramref name="rule"/>) for every form SPEC §0.6
        /// rejects at settings time: non-grammar shapes, uppercase hosts or registry
        /// segments, credential-bearing URLs, HTTP, file or relative paths,
        /// localhost/loopback/link-local hosts, whitespace, ambiguous percent
        /// escapes, <c>..</c>, unknown schemes and query/fragment baggage.
        /// </summary>
        public static bool TryParse(string? entry, out PackageSourceRule? rule)
        {
            rule = null;
            if (entry is null || entry.Length == 0 || !IsAsciiPrintable(entry))
            {
                return false;
            }
            if (entry.StartsWith("registry:", StringComparison.Ordinal))
            {
                return TryParseRegistry(entry, entry.Substring("registry:".Length), out rule);
            }
            if (entry.StartsWith("git:", StringComparison.Ordinal))
            {
                return TryParseGit(entry, entry.Substring("git:".Length), out rule);
            }
            return false;
        }

        /// <summary>
        /// True when this rule allows a registry candidate whose package name is
        /// <paramref name="packageName"/>: ordinal equality for
        /// <see cref="PackageSourceKind.RegistryName"/>, ordinal prefix match for
        /// <see cref="PackageSourceKind.RegistryPrefix"/> (so <c>com.unity.*</c>
        /// matches <c>com.unity.x</c> but never <c>com.unityevil</c>).
        /// </summary>
        public bool AllowsRegistryName(string packageName)
        {
            if (packageName is null)
            {
                return false;
            }
            switch (Kind)
            {
                case PackageSourceKind.RegistryName:
                    return string.Equals(RegistryName, packageName, StringComparison.Ordinal);
                case PackageSourceKind.RegistryPrefix:
                    return packageName.StartsWith(RegistryPrefix!, StringComparison.Ordinal);
                default:
                    return false;
            }
        }

        /// <summary>
        /// True when this rule is a Git rule whose normalized origin equals
        /// <paramref name="normalizedOrigin"/> ordinally — case-sensitive path,
        /// preserved trailing <c>.git</c> and explicit-port equality
        /// (SPEC §0.6 SG-19).
        /// </summary>
        public bool AllowsGitOrigin(string normalizedOrigin) =>
            Kind == PackageSourceKind.GitOrigin &&
            string.Equals(NormalizedGitOrigin, normalizedOrigin, StringComparison.Ordinal);

        // ── registry: arm ─────────────────────────────────────────────────────

        private static bool TryParseRegistry(
            string entry, string spec, out PackageSourceRule? rule)
        {
            rule = null;
            if (spec.EndsWith(".*", StringComparison.Ordinal))
            {
                var name = spec.Substring(0, spec.Length - 2);
                if (!IsValidRegistryName(name))
                {
                    return false;
                }
                rule = new PackageSourceRule(
                    entry, PackageSourceKind.RegistryPrefix, null, name + ".", null);
                return true;
            }
            // A "*" anywhere else (or a bare "*"/"**" tail) is outside the grammar.
            if (spec.IndexOf('*') >= 0 || !IsValidRegistryName(spec))
            {
                return false;
            }
            rule = new PackageSourceRule(
                entry, PackageSourceKind.RegistryName, spec, null, null);
            return true;
        }

        /// <summary>
        /// The dot-separated package-name grammar shared by entries and candidate
        /// ids: each segment is <c>[a-z0-9][a-z0-9_-]*</c>.
        /// </summary>
        internal static bool IsValidRegistryName(string? name)
        {
            if (name is null || name.Length == 0)
            {
                return false;
            }
            var start = 0;
            for (var i = 0; i <= name.Length; i++)
            {
                if (i != name.Length && name[i] != '.')
                {
                    continue;
                }
                var length = i - start;
                if (length == 0 || !IsLowerAlnum(name[start]))
                {
                    return false;
                }
                for (var j = start + 1; j < i; j++)
                {
                    var c = name[j];
                    if (!IsLowerAlnum(c) && c != '_' && c != '-')
                    {
                        return false;
                    }
                }
                start = i + 1;
            }
            return true;
        }

        // ── git: arm ─────────────────────────────────────────────────────────

        private static bool TryParseGit(string entry, string spec, out PackageSourceRule? rule)
        {
            rule = null;
            var form = ClassifyGitSource(spec, out var split);
            if (form == GitSourceForm.Url)
            {
                var scheme = spec.Substring(0, split);
                var rest = spec.Substring(split + "://".Length);
                string prefix;
                if (string.Equals(scheme, "https", StringComparison.Ordinal))
                {
                    prefix = "https://";
                }
                else if (
                    string.Equals(scheme, "ssh", StringComparison.Ordinal) &&
                    rest.StartsWith("git@", StringComparison.Ordinal))
                {
                    prefix = "ssh://git@";
                    rest = rest.Substring("git@".Length);
                }
                else
                {
                    return false;
                }
                if (!TryParseUrlOrigin(rest, out var normalized))
                {
                    return false;
                }
                rule = new PackageSourceRule(
                    entry, PackageSourceKind.GitOrigin, null, null, prefix + normalized);
                return true;
            }
            if (form == GitSourceForm.Scp)
            {
                var authority = spec.Substring(0, split);
                var path = spec.Substring(split + 1);
                if (!authority.StartsWith("git@", StringComparison.Ordinal))
                {
                    return false;
                }
                var host = authority.Substring("git@".Length);
                if (!IsValidGitHost(host) || !IsValidRepoPath(path))
                {
                    return false;
                }
                rule = new PackageSourceRule(
                    entry, PackageSourceKind.GitOrigin, null, null, authority + ":" + path);
                return true;
            }
            return false;
        }

        /// <summary>
        /// The one-time form decision shared with <see cref="PackageIdSource"/>:
        /// a URL form is recognized ONLY when <paramref name="text"/> starts
        /// with '<c>&lt;scheme&gt;://</c>' — scheme = ALPHA *(ALPHA / DIGIT /
        /// '+' / '-' / '.'); otherwise an SCP form is recognized by
        /// '<c>[user@]host:path</c>', where the FIRST ':' that no '/' precedes
        /// is the host/path boundary and everything after it is repository
        /// path ('@', ':', a '://' run, '=', ',' are ordinary pchar text
        /// there). On return <paramref name="split"/> is the boundary index —
        /// the ':' ending the URL scheme or the SCP host/path ':' — or -1 for
        /// <see cref="GitSourceForm.Other"/>.
        /// </summary>
        internal static GitSourceForm ClassifyGitSource(string text, out int split)
        {
            split = -1;
            if (text.Length > 0 && IsSchemeFirstChar(text[0]))
            {
                var i = 1;
                while (i < text.Length && IsSchemeChar(text[i]))
                {
                    i++;
                }
                if (i + 2 < text.Length &&
                    text[i] == ':' && text[i + 1] == '/' && text[i + 2] == '/')
                {
                    split = i;
                    return GitSourceForm.Url;
                }
            }
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '/')
                {
                    break;
                }
                if (text[i] == ':')
                {
                    split = i;
                    return GitSourceForm.Scp;
                }
            }
            return GitSourceForm.Other;
        }

        /// <summary>
        /// Parses <c>&lt;host&gt;[:&lt;port&gt;]/&lt;absolute-repo-path&gt;</c> — the
        /// part of a <c>https://</c> or <c>ssh://git@</c> entry after the scheme
        /// (and fixed <c>git@</c>) — and returns the normalized origin tail
        /// (<c>host[:port]/path</c>) unchanged, since the grammar already forces
        /// the normalized spelling.
        /// </summary>
        private static bool TryParseUrlOrigin(string hostPortPath, out string normalized)
        {
            normalized = "";
            var slash = hostPortPath.IndexOf('/');
            if (slash <= 0)
            {
                return false;
            }
            var hostPort = hostPortPath.Substring(0, slash);
            var path = hostPortPath.Substring(slash); // includes the leading '/'
            if (path.Length < 2 || !IsValidRepoPath(path))
            {
                return false;
            }
            var host = hostPort;
            var colon = hostPort.LastIndexOf(':');
            if (colon >= 0)
            {
                if (!IsValidPort(hostPort.Substring(colon + 1)))
                {
                    return false;
                }
                host = hostPort.Substring(0, colon);
                if (host.IndexOf(':') >= 0)
                {
                    // A second ':' would be an unbracketed IPv6 literal.
                    return false;
                }
            }
            if (!IsValidGitHost(host))
            {
                return false;
            }
            normalized = hostPort + path;
            return true;
        }

        internal static bool IsValidPort(string port)
        {
            if (port.Length == 0 || port.Length > 5)
            {
                return false;
            }
            var value = 0;
            for (var i = 0; i < port.Length; i++)
            {
                var c = port[i];
                if (c < '0' || c > '9')
                {
                    return false;
                }
                value = (value * 10) + (c - '0');
            }
            return value >= 1 && value <= 65535;
        }

        /// <summary>
        /// The <c>&lt;lowercase-host&gt;</c> arm of the grammar: a DNS name or a
        /// canonical dotted-quad IPv4 literal, already lowercase, and never
        /// localhost, loopback or link-local. Bracketed IPv6 literals and
        /// non-canonical numeric forms (single all-digit labels, <c>0x</c> labels,
        /// leading-zero or short quads) are ambiguous and rejected.
        /// </summary>
        internal static bool IsValidGitHost(string host)
        {
            if (host.Length == 0)
            {
                return false;
            }
            var bare = host.EndsWith(".", StringComparison.Ordinal)
                ? host.Substring(0, host.Length - 1)
                : host;
            if (bare.Length == 0 ||
                string.Equals(bare, "localhost", StringComparison.Ordinal) ||
                bare.EndsWith(".localhost", StringComparison.Ordinal))
            {
                return false;
            }
            var onlyDigitsAndDots = true;
            var labels = bare.Split('.');
            for (var i = 0; i < labels.Length; i++)
            {
                var label = labels[i];
                if (label.Length == 0)
                {
                    return false;
                }
                var allDigits = true;
                for (var j = 0; j < label.Length; j++)
                {
                    var c = label[j];
                    if (!IsLowerAlnum(c) && c != '-')
                    {
                        return false;
                    }
                    if (c < '0' || c > '9')
                    {
                        allDigits = false;
                    }
                }
                if (label[0] == '-' || label[label.Length - 1] == '-')
                {
                    return false;
                }
                if (label.StartsWith("0x", StringComparison.Ordinal) ||
                    label.StartsWith("0X", StringComparison.Ordinal))
                {
                    // Hex-splashed IP spellings (0x7f.0.0.1) are ambiguous.
                    return false;
                }
                onlyDigitsAndDots &= allDigits;
            }
            if (!onlyDigitsAndDots)
            {
                return true;
            }
            // All-numeric labels: only a canonical dotted quad is unambiguous.
            if (labels.Length != 4)
            {
                return false;
            }
            var octets = new int[4];
            for (var i = 0; i < 4; i++)
            {
                var label = labels[i];
                if (label.Length > 3 || (label.Length > 1 && label[0] == '0'))
                {
                    return false;
                }
                var value = 0;
                for (var j = 0; j < label.Length; j++)
                {
                    value = (value * 10) + (label[j] - '0');
                }
                if (value > 255)
                {
                    return false;
                }
                octets[i] = value;
            }
            // Loopback 127.0.0.0/8, link-local 169.254.0.0/16 and the
            // this-network 0.0.0.0/8 block (conservative) are never allowed.
            return octets[0] != 127 && octets[0] != 0 &&
                !(octets[0] == 169 && octets[1] == 254);
        }

        /// <summary>
        /// The repo-path arm of the grammar: the RFC 3986 <c>pchar</c> set —
        /// unreserved <c>[A-Za-z0-9._\-~]</c>, sub-delims <c>!$&amp;'()*+,;=</c>,
        /// <c>:</c> and <c>@</c> — plus the <c>/</c> segment separator and
        /// unambiguous percent escapes; rejects <c>..</c>, whitespace,
        /// queries/fragments and every escape that is malformed or decodes
        /// outside <c>[A-Za-z0-9_-~]</c> (so <c>%2e</c>/<c>%2f</c>/<c>%20</c>
        /// can never smuggle structure).
        /// </summary>
        internal static bool IsValidRepoPath(string path)
        {
            if (path.Length == 0 || path.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                return false;
            }
            for (var i = 0; i < path.Length; i++)
            {
                var c = path[i];
                if (IsPathChar(c))
                {
                    continue;
                }
                if (c != '%' || !IsUnambiguousEscape(path, i))
                {
                    return false;
                }
                i += 2;
            }
            return true;
        }

        /// <summary>
        /// RFC 3986 <c>pchar</c> minus percent escapes: unreserved
        /// <c>[A-Za-z0-9\-._~]</c>, sub-delims <c>!$&amp;'()*+,;=</c>, <c>:</c>
        /// and <c>@</c>, plus the <c>/</c> segment separator. <c>?</c> and
        /// <c>#</c> stay excluded so queries and fragments remain rejected, and
        /// <c>[</c>/<c>]</c> stay excluded so a bracketed host literal can never
        /// hide inside a path.
        /// </summary>
        private static bool IsPathChar(char c) =>
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
            (c >= '0' && c <= '9') ||
            c == '-' || c == '.' || c == '_' || c == '~' ||
            c == '!' || c == '$' || c == '&' || c == '\'' ||
            c == '(' || c == ')' || c == '*' || c == '+' ||
            c == ',' || c == ';' || c == '=' ||
            c == ':' || c == '@' || c == '/';

        /// <summary>
        /// A <c>%XX</c> escape at <paramref name="index"/> is unambiguous only
        /// when both digits are hex and the decoded byte is itself a safe
        /// character <c>[A-Za-z0-9_-~]</c> — anything structural stays literal-only.
        /// </summary>
        private static bool IsUnambiguousEscape(string text, int index)
        {
            if (index + 2 >= text.Length)
            {
                return false;
            }
            var hi = HexValue(text[index + 1]);
            var lo = HexValue(text[index + 2]);
            if (hi < 0 || lo < 0)
            {
                return false;
            }
            var decoded = (char)((hi << 4) | lo);
            return (decoded >= 'a' && decoded <= 'z') ||
                (decoded >= 'A' && decoded <= 'Z') ||
                (decoded >= '0' && decoded <= '9') ||
                decoded == '_' || decoded == '-' || decoded == '~';
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9')
            {
                return c - '0';
            }
            if (c >= 'a' && c <= 'f')
            {
                return c - 'a' + 10;
            }
            if (c >= 'A' && c <= 'F')
            {
                return c - 'A' + 10;
            }
            return -1;
        }

        /// <summary>ASCII lowercase of <paramref name="c"/>; other characters are returned unchanged.</summary>
        internal static char AsciiLower(char c) =>
            c >= 'A' && c <= 'Z' ? (char)(c + ('a' - 'A')) : c;

        private static bool IsLowerAlnum(char c) =>
            (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');

        /// <summary>An ALPHA character — the only valid URL scheme start.</summary>
        private static bool IsSchemeFirstChar(char c) =>
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

        /// <summary>ALPHA / DIGIT / '+' / '-' / '.' — the URL scheme continuation set.</summary>
        private static bool IsSchemeChar(char c) =>
            IsSchemeFirstChar(c) || (c >= '0' && c <= '9') ||
            c == '+' || c == '-' || c == '.';

        /// <summary>Printable ASCII only — no whitespace, controls or non-ASCII.</summary>
        internal static bool IsAsciiPrintable(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c < 0x21 || c > 0x7E)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
