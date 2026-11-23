#nullable enable

using System.Text.RegularExpressions;

namespace OpWeave.Core
{
    /// <summary>
    /// The fixed regular expressions shared by the unity.* JSON-shape codecs
    /// (T-W1.4-05). Every pattern is culture-invariant and deterministic;
    /// none of them may change without changing the JSON-shape contract.
    /// </summary>
    /// <remarks>
    /// Where the SPEC does not fix a key's grammar, the pattern documents the
    /// local, reversible choice: <see cref="TypeName"/> is the SPEC §B1
    /// <c>DotNetName</c> grammar (one or more dot-separated identifiers),
    /// <see cref="HierarchyPath"/> is a leading-<c>/</c> chain of non-empty
    /// segments in which a <c>\/</c> pair is one escaped name character, not
    /// a segment boundary (SPEC §G2: a <c>/</c> inside a GameObject name is
    /// written <c>\/</c>; a backslash followed by any other character stays
    /// an ordinary name character), and
    /// <see cref="ClassId"/> accepts the SPEC §B5 class-id forms —
    /// <c>type:&lt;fully qualified name&gt;</c> and the built-in
    /// <c>unity.*</c> short names — together with a plain fully qualified
    /// name, since the JSON <c>type</c> key of a ComponentRef is not a
    /// Type ID argument.
    /// </para>
    /// <para>
    /// Full-match patterns anchor the end with <c>\z</c>, not <c>$</c>: in
    /// .NET regular expressions <c>$</c> also matches immediately before a
    /// trailing <c>\n</c>, which would let a newline-suffixed string pass a
    /// whole-string grammar and let Normalize silently drop the newline.
    /// </para>
    /// </remarks>
    internal static class UnityShapePatterns
    {
        /// <summary>The SPEC §B3 asset-path prefix: <c>^(Assets|Packages)/</c>.</summary>
        internal static readonly Regex AssetPath = new Regex(
            @"^(Assets|Packages)/", RegexOptions.CultureInvariant);

        /// <summary>
        /// The SPEC §B3 hierarchy path: <c>/Root/Child</c> chains of non-empty
        /// segments, where SPEC §G2's escape writes a <c>/</c> inside a
        /// GameObject name as the two characters <c>\/</c> — the alternative
        /// keeps that pair inside its segment instead of splitting on the
        /// slash. A backslash not followed by <c>/</c> is an ordinary name
        /// character (SPEC defines no other escape). The three alternatives
        /// are disjoint — the <c>\/</c> pair, a <c>\</c> not followed by
        /// <c>/</c>, or a character that is neither — so a backslash is
        /// consumed exactly one way and a long chain of escaped slashes
        /// cannot repartition into exponentially many parses.
        /// </summary>
        internal static readonly Regex HierarchyPath = new Regex(
            @"^/(?:\\/|\\(?!/)|[^\\/])+(?:/(?:\\/|\\(?!/)|[^\\/])+)*\z", RegexOptions.CultureInvariant);

        /// <summary>The SPEC §B1 <c>DotNetName</c> grammar used for unity.TypeName.</summary>
        internal static readonly Regex TypeName = new Regex(
            @"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)*\z", RegexOptions.CultureInvariant);

        /// <summary>A class-id string: an optional <c>type:</c> prefix on a dotted name.</summary>
        internal static readonly Regex ClassId = new Regex(
            @"^(type:)?[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)*\z", RegexOptions.CultureInvariant);

        /// <summary>The SPEC §B4 color string: <c>#RRGGBBAA</c> (8 hex digits, either case).</summary>
        internal static readonly Regex ColorHex = new Regex(
            @"^#[0-9A-Fa-f]{8}\z", RegexOptions.CultureInvariant);

        /// <summary>A Ref <c>handle</c> session key written as decimal digits (SPEC §B5, L-04).</summary>
        internal static readonly Regex HandleDigits = new Regex(
            @"^-?[0-9]+\z", RegexOptions.CultureInvariant);
    }
}
