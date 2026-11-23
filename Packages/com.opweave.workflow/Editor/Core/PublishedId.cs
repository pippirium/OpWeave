#nullable enable

using System;
using System.Security.Cryptography;
using System.Text;

namespace OpWeave.Core
{
    /// <summary>
    /// The publishable form of a contract ID or port-type string at the
    /// Registry publication boundary (SPEC §C3 error paragraph): the text
    /// itself when it satisfies the applicable ID grammar, otherwise the
    /// fixed <see cref="Placeholder"/>. A malformed ID is never published
    /// verbatim — its unvalidated text may carry credentials no pattern
    /// mask reliably finds, for example a bare
    /// '<c>password=…</c>' assignment behind no URL or parameter marker.
    /// </summary>
    /// <remarks>
    /// Grammar-valid IDs are returned unchanged: the SPEC §B1 Type ID /
    /// TypeRef grammar and the §A4 Operation/scoped grammars admit none
    /// of '<c>=</c>' or '<c>@</c>', and '<c>:</c>' only inside a
    /// '<c>type:</c>' class-id generic argument — a valid ID cannot carry
    /// a credential. Values that are not contract IDs — a class's full
    /// name, an assembly name, a free-text detail — are not routed here;
    /// they keep passing through <see cref="CredentialMask"/> at the
    /// record boundary.
    /// </remarks>
    internal static class PublishedId
    {
        private const string GenericMarker = "<>";

        /// <summary>
        /// The safe stand-in for a malformed ID: a fixed marker plus the
        /// raw length and a short lowercase SHA-256 prefix of the raw
        /// UTF-8 — enough to correlate equal offers (for example both
        /// sides of a duplicate-ID verdict) without exposing the text.
        /// </summary>
        internal static string Placeholder(string rawText)
        {
            byte[] hash;
            using (var sha256 = SHA256.Create())
            {
                hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawText));
            }
            const string digits = "0123456789abcdef";
            var chars = new char[8];
            for (var i = 0; i < 4; i++)
            {
                chars[i * 2] = digits[hash[i] >> 4];
                chars[i * 2 + 1] = digits[hash[i] & 0xF];
            }
            return $"<malformed id len={rawText.Length} sha256={new string(chars)}>";
        }

        /// <summary>
        /// The publishable form of an offered Type ID whose declared
        /// generic arity is unknown — an observed fact of the §X.3.2
        /// gate: <paramref name="id"/> when its base name, minus an
        /// optional '<c>&lt;&gt;</c>' marker, is a canonical SPEC §B1
        /// Type ID name (exact parse round-trip, no generic arguments),
        /// otherwise <see cref="Placeholder"/>.
        /// </summary>
        internal static string TypeId(string id)
        {
            var name = id.EndsWith(GenericMarker, StringComparison.Ordinal)
                ? id.Substring(0, id.Length - GenericMarker.Length)
                : id;
            return IsCanonicalTypeName(name) ? id : Placeholder(id);
        }

        /// <summary>
        /// The publishable form of an offered Type ID whose declared
        /// <paramref name="genericArity"/> is known: the
        /// '<c>&lt;&gt;</c>' marker must additionally be present exactly
        /// when the arity is positive (the
        /// <see cref="TypeRegistryBuilder"/> screening rule).
        /// </summary>
        internal static string TypeId(string id, int genericArity)
        {
            var hasMarker = id.EndsWith(GenericMarker, StringComparison.Ordinal);
            if (hasMarker != genericArity > 0)
            {
                return Placeholder(id);
            }
            return TypeId(id);
        }

        /// <summary>
        /// The publishable form of an Operation, extension or manifest
        /// Provider ID — the SPEC §A4 Operation id grammar: the ID
        /// itself when grammar-valid, otherwise <see cref="Placeholder"/>.
        /// </summary>
        internal static string OperationId(string id) =>
            IdGrammar.IsValidOperationId(id) ? id : Placeholder(id);

        /// <summary>
        /// The publishable form of a scoped ID — a port or control-port
        /// name — under the SPEC §A4 scoped grammar.
        /// </summary>
        internal static string ScopedName(string name) =>
            IdGrammar.IsValidScopedId(name) ? name : Placeholder(name);

        /// <summary>
        /// The publishable form of a port's type text — a SPEC §B1
        /// TypeRef, generic arguments included: the raw text when it
        /// parses, otherwise <see cref="Placeholder"/>.
        /// </summary>
        internal static string PortType(string text) =>
            TypeRef.Parse(text).Ok ? text : Placeholder(text);

        // The canonical SPEC §B1 Type ID name check: the name parses,
        // carries no generic arguments and round-trips to the identical
        // text — no tolerated whitespace or other non-canonical spelling.
        private static bool IsCanonicalTypeName(string name)
        {
            var parsed = TypeRef.Parse(name);
            return parsed.Ok && parsed.Value!.Args.Count == 0 &&
                string.Equals(parsed.Value!.ToString(), name, StringComparison.Ordinal);
        }
    }
}
