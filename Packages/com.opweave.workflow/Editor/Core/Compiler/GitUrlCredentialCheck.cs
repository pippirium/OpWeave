#nullable enable

using System;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The SPEC §G8.1 credential detection applied to a constant
    /// <c>unity.package.add</c> <c>id</c> during validation (SPEC §D6 item 3):
    /// userinfo on any non-SSH URL (<c>user@</c>, <c>user:password@</c>,
    /// <c>token@</c>), a password part (<c>:</c> and after) in any userinfo —
    /// including SSH- and SCP-shaped text — and query parameters other than
    /// UPM's <c>path</c> (<c>?access_token=…</c>) count as credentials. An
    /// SSH-form user name without a password part
    /// (<c>git@host:org/repo.git</c>, <c>ssh://git@host/…</c>) is not a
    /// credential, and a form that cannot be decided is treated as
    /// credential-bearing (fail closed, "判定できない形は安全側で拒否する").
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the compile-time twin of the identical SPEC text implemented by
    /// <c>OpWeave.UnityAdapter</c>'s <c>PackageIdSource.ContainsCredentials</c>
    /// for the settings-allowlist and pre-side-effect re-check path
    /// (T-W2.1-07/T-W2.1-08). The D6 phase lives in <c>OpWeave.Core</c>, which
    /// cannot reference the adapter assembly, and neither side may expose the
    /// rule as public API — so both sides keep an internal copy that must stay
    /// aligned with SPEC §G8.1 and with each other.
    /// </para>
    /// <para>
    /// The check decides the candidate form the same way the adapter's parser
    /// does: an authority — and therefore URL userinfo — exists only when the
    /// text starts with '<c>&lt;scheme&gt;://</c>'. Inside an SCP-form
    /// candidate the '<c>://</c>' and '<c>@</c>' characters of the repository
    /// path are ordinary path text and are never read as a scheme separator or
    /// a userinfo delimiter, while a '<c>:</c>' before the first '<c>@</c>'
    /// may still be a password part (<c>user:&lt;secret&gt;@host:…</c>) and
    /// stays fail-closed credentials.
    /// </para>
    /// </remarks>
    internal static class GitUrlCredentialCheck
    {
        /// <summary>The candidate form the credential rule distinguishes.</summary>
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
        /// True when <paramref name="packageId"/> carries the SPEC §G8.1
        /// credential forms. Null and empty ids are not credential-bearing —
        /// their handling belongs to the port's required/codec checks.
        /// </summary>
        internal static bool ContainsCredentials(string? packageId)
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
            if (ClassifyGitSource(text, out var split) == GitSourceForm.Url)
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
        /// The one-time form decision: a URL form is recognized ONLY when
        /// <paramref name="text"/> starts with '<c>&lt;scheme&gt;://</c>' —
        /// scheme = ALPHA *(ALPHA / DIGIT / '+' / '-' / '.'); otherwise an SCP
        /// form is recognized by '<c>[user@]host:path</c>', where the FIRST ':'
        /// that no '/' precedes is the host/path boundary and everything after
        /// it is repository path ('@', ':', a '://' run, '=', ',' are ordinary
        /// pchar text there). On return <paramref name="split"/> is the
        /// boundary index — the ':' ending the URL scheme or the SCP host/path
        /// ':' — or -1 for <see cref="GitSourceForm.Other"/>.
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

        private static bool IsScheme(string scheme, string expected)
        {
            if (scheme.Length != expected.Length)
            {
                return false;
            }
            for (var i = 0; i < scheme.Length; i++)
            {
                if (AsciiLower(scheme[i]) != expected[i])
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>ASCII lowercase of <paramref name="c"/>; other characters are returned unchanged.</summary>
        private static char AsciiLower(char c) =>
            c >= 'A' && c <= 'Z' ? (char)(c + ('a' - 'A')) : c;

        /// <summary>An ALPHA character — the only valid URL scheme start.</summary>
        private static bool IsSchemeFirstChar(char c) =>
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

        /// <summary>ALPHA / DIGIT / '+' / '-' / '.' — the URL scheme continuation set.</summary>
        private static bool IsSchemeChar(char c) =>
            IsSchemeFirstChar(c) || (c >= '0' && c <= '9') ||
            c == '+' || c == '-' || c == '.';
    }
}
