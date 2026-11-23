#nullable enable

using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core
{
    /// <summary>
    /// The SPEC §F6 credential mask applied before a diagnostic or an
    /// Operation log entry is persisted: values of known credential keys
    /// (<c>password</c>/<c>token</c>/<c>secret</c>/<c>authorization</c> and
    /// the common forms <c>access_token</c>, <c>api_key</c>/<c>apikey</c>,
    /// <c>client_secret</c>, <c>private_key</c>, <c>credentials</c>,
    /// <c>pwd</c>, <c>key</c>, <c>auth</c> and <c>sig</c>/<c>signature</c>)
    /// and the userinfo or credential query/fragment parameter of a URL
    /// of any scheme are replaced by <see cref="Replacement"/>. A free-text
    /// secret no key or URL shape reveals stays the Operation's own
    /// contract (SPEC §F6).
    /// </summary>
    /// <remarks>
    /// Key matching is deliberately broad: the key is normalized (case
    /// folded, '<c>_</c>', '<c>-</c>' and '<c>.</c>' removed) and masked when
    /// it contains a credential word, so snake/kebab/camel and prefixed
    /// forms (<c>clientSecret</c>, <c>x-api-key</c>, <c>AWS_SECRET</c>) all
    /// fold to the same mask — masking a harmless field is cheap, leaking a
    /// credential is not. URL masking covers every RFC 3986
    /// '<c>&lt;scheme&gt;://…@</c>' userinfo whatever the scheme and every
    /// credential-named parameter after '<c>?</c>', '<c>&amp;</c>',
    /// '<c>;</c>' or '<c>#</c>' wherever they appear in the text — a bare
    /// user name is masked with the password-bearing forms alike, matching
    /// the fail-closed reading of SPEC §G8.1 — while SCP-shape text without
    /// a scheme is left alone (its password-bearing detection lives in
    /// <c>OpWeave.Core.Compiler.GitUrlCredentialCheck</c>, which answers no
    /// masked form).
    /// </remarks>
    internal static class CredentialMask
    {
        /// <summary>The fixed replacement every masked value becomes.</summary>
        internal const string Replacement = "***";

        // The credential words a normalized key is matched against
        // (SPEC §F6's list plus the common token/key forms).
        private static readonly string[] s_secretParts =
        {
            "password", "token", "secret", "authorization",
            "credential", "apikey", "privatekey",
            "pwd", "key", "auth", "sig",
        };

        // Every '<scheme>://userinfo@' occurrence — the RFC 3986 scheme
        // grammar (ALPHA *(ALPHA / DIGIT / '+' / '-' / '.')), so the mask
        // is not limited to http(s)/ssh/git and an ftp:// or
        // custom+scheme:// authority loses its userinfo the same way.
        // Each match is independent of the preceding character (no \b
        // before the scheme), so a '+', '-', '://' or an earlier URL can
        // never hide a credential.
        private static readonly Regex s_urlUserinfo = new Regex(
            "(?i)(?<scheme>[a-z][a-z0-9+.-]*)://(?<userinfo>[^/\\s?#@]+)@",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // A credential-named query or fragment parameter anywhere in the
        // text — '?name=', '&name=', ';name=' or '#name=' — that matches
        // ONLY credential keys: the s_secretParts words are the single
        // source, each expressed on the raw name with '_', '-' and '.'
        // allowed between its letters and arbitrary name characters
        // around it — the IsSecretKey fold-then-contains rule as a
        // pattern. A non-credential parameter never takes part in a
        // match, so its value's text is still examined — a '?token='
        // nested inside another parameter's value is masked all the same,
        // while UPM's non-credential 'path=' passes through (SPEC §G8.1).
        // The value runs to the next '&', '#', ';', '?', quote, angle
        // bracket or whitespace/control.
        private static readonly Regex s_credentialParameter = new Regex(
            "(?i)(?<prefix>[?&;#][^&=\\s?#;\"'<>\\p{Cc}]*(?:" +
            CredentialNameAlternation() +
            ")[^&=\\s?#;\"'<>\\p{Cc}]*=)(?<value>[^&#;?\\s\"'<>\\p{Cc}]*)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Returns <paramref name="value"/> with every credential member and
        /// URL userinfo masked — a fresh value; the argument is never
        /// mutated.
        /// </summary>
        internal static JsonValue Mask(JsonValue value) =>
            JsonValue.From(MaskToken(value.ToToken()));

        /// <summary>
        /// The token-level form of <see cref="Mask(JsonValue)"/>: a masked
        /// copy is built; the argument tree is never mutated. Object members
        /// whose key is a credential key collapse to
        /// <c>"***"</c> regardless of the value's shape, and every remaining
        /// string scalar is URL-masked.
        /// </summary>
        internal static JToken MaskToken(JToken token)
        {
            switch (token)
            {
                case JObject obj:
                    var masked = new JObject();
                    foreach (var property in obj.Properties())
                    {
                        masked[property.Name] = IsSecretKey(property.Name)
                            ? new JValue(Replacement)
                            : MaskToken(property.Value);
                    }
                    return masked;
                case JArray array:
                    var items = new JArray();
                    foreach (var item in array)
                    {
                        items.Add(MaskToken(item));
                    }
                    return items;
                case JValue scalar when scalar.Type == JTokenType.String:
                    return new JValue(MaskText((string)scalar.Value!));
                default:
                    return token;
            }
        }

        /// <summary>
        /// Masks the credential parts of every URL inside
        /// <paramref name="text"/>: the userinfo of any scheme's authority
        /// (<c>https://user:pass@host/r.git</c> →
        /// <c>https://***@host/r.git</c>,
        /// <c>ftp://user:pass@host/t</c> → <c>ftp://***@host/t</c>) and
        /// the value of each query or fragment parameter whose name is a
        /// credential key (<c>?access_token=t&amp;path=sub</c> →
        /// <c>?access_token=***&amp;path=sub</c>,
        /// <c>#token=t</c> → <c>#token=***</c>, UPM's non-credential
        /// <c>path</c> kept — SPEC §G8.1). Both scans are whole-text
        /// regular expressions so a URL start is examined wherever it
        /// appears — a '+', '-', '://' or an earlier URL never hides a
        /// later credential, and because the parameter regex matches
        /// credential keys only, a non-credential parameter's value can
        /// never consume and hide one either. Text without a credential
        /// is returned unchanged.
        /// </summary>
        internal static string MaskText(string text) =>
            s_credentialParameter.Replace(
                s_urlUserinfo.Replace(text, "${scheme}://***@"),
                "${prefix}" + Replacement);

        /// <summary>
        /// The masked copy of one published <see cref="Diagnostic"/>: every
        /// string field — the message an emitter may have interpolated an
        /// unvalidated ID or other unsafe text into, the hint and the
        /// context fields alike — passes through <see cref="MaskText"/>,
        /// while the code, severity and retryable flag stay unchanged
        /// (SPEC §C3 error paragraph, §F6).
        /// </summary>
        internal static Diagnostic MaskDiagnostic(Diagnostic diagnostic) =>
            diagnostic with
            {
                Code = MaskText(diagnostic.Code),
                Message = MaskText(diagnostic.Message),
                NodeId = diagnostic.NodeId is null ? null : MaskText(diagnostic.NodeId),
                Port = diagnostic.Port is null ? null : MaskText(diagnostic.Port),
                JsonPath = diagnostic.JsonPath is null ? null : MaskText(diagnostic.JsonPath),
                Hint = diagnostic.Hint is null ? null : MaskText(diagnostic.Hint),
            };

        // The credential-key alternation inside a query parameter name,
        // built from s_secretParts so the JSON keys and URL parameters
        // share one source: each word's letters with '_', '-' and '.'
        // allowed between them — the IsSecretKey fold-then-contains rule
        // expressed on the raw name.
        private static string CredentialNameAlternation()
        {
            var parts = new string[s_secretParts.Length];
            for (var i = 0; i < s_secretParts.Length; i++)
            {
                var word = s_secretParts[i];
                var builder = new StringBuilder(word.Length * 8);
                for (var c = 0; c < word.Length; c++)
                {
                    if (c != 0)
                    {
                        builder.Append("[-_.]*");
                    }
                    builder.Append(Regex.Escape(word[c].ToString()));
                }
                parts[i] = builder.ToString();
            }
            return string.Join("|", parts);
        }

        // True when <paramref name="key"/> names a credential: case-folded
        // with '_', '-' and '.' removed, then matched against the
        // credential words.
        private static bool IsSecretKey(string key)
        {
            var normalized = new StringBuilder(key.Length);
            for (var i = 0; i < key.Length; i++)
            {
                var c = key[i];
                if (c != '_' && c != '-' && c != '.')
                {
                    normalized.Append(char.ToLowerInvariant(c));
                }
            }
            var folded = normalized.ToString();
            for (var i = 0; i < s_secretParts.Length; i++)
            {
                if (folded.Contains(s_secretParts[i]))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
