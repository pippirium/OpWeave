#nullable enable

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace OpWeave.Operations
{
    /// <summary>
    /// The identifier grammar <c>unity.script.create_from_template</c>
    /// validates <c>className</c> and <c>namespace</c> against (SPEC §G8
    /// item 1): the SPEC §B1 <c>DotNetName</c> shape — an ASCII letter,
    /// then letters, digits and underscores — with the C# reserved
    /// keywords excluded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shape is a strict subset of every identifier the C# grammar
    /// accepts — <c>_name</c>, <c>@verbatim</c> and non-ASCII letters do
    /// not pass — chosen so the declared <c>typeName</c> output, typed
    /// <c>unity.TypeName</c> (§I.4), is well-formed for every accepted
    /// input: the Codec's grammar is this same DotNetName shape, so a
    /// broader C# reading would let a step succeed and then fail its own
    /// output validation (§E5, OW-RUN-007). The substitution is verbatim,
    /// so a C# reserved keyword — legal as an identifier only under the
    /// <c>@</c> prefix the templates never write — is rejected too: a
    /// <c>class int</c> the template would emit could never compile.
    /// Rejection surfaces as OW-OP-006, the §J constraint-violation code,
    /// from the port <see cref="OpWeave.Core.PortConstraints"/> pattern at
    /// D3 for the character shape and from the Operation for the keyword
    /// half — and again at step time for a value arriving through a data
    /// edge.
    /// </para>
    /// </remarks>
    internal static class ScriptIdentifiers
    {
        /// <summary>The <see cref="OpWeave.Core.PortConstraints"/> pattern of <c>className</c>.</summary>
        internal const string IdentifierPattern = @"\A[A-Za-z][A-Za-z0-9_]*\z";

        /// <summary>The <see cref="OpWeave.Core.PortConstraints"/> pattern of <c>namespace</c>.</summary>
        internal const string NamespacePattern =
            @"\A[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)*\z";

        private static readonly Regex s_shape = new Regex(
            IdentifierPattern, RegexOptions.CultureInvariant);

        // The C# reserved keywords — the words the grammar reserves
        // unconditionally. Contextual keywords (var, record, value, ...)
        // stay legal identifiers and are not listed.
        private static readonly HashSet<string> s_keywords = new HashSet<string>(
            new[]
            {
                "abstract", "as", "base", "bool", "break", "byte", "case",
                "catch", "char", "checked", "class", "const", "continue",
                "decimal", "default", "delegate", "do", "double", "else",
                "enum", "event", "explicit", "extern", "false", "finally",
                "fixed", "float", "for", "foreach", "goto", "if", "implicit",
                "in", "int", "interface", "internal", "is", "lock", "long",
                "namespace", "new", "null", "object", "operator", "out",
                "override", "params", "private", "protected", "public",
                "readonly", "ref", "return", "sbyte", "sealed", "short",
                "sizeof", "stackalloc", "static", "string", "struct",
                "switch", "this", "throw", "true", "try", "typeof", "uint",
                "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
                "void", "volatile", "while",
            },
            StringComparer.Ordinal);

        /// <summary>
        /// Whether <paramref name="text"/> is usable as a class name
        /// segment: the DotNetName shape and not a reserved keyword.
        /// </summary>
        internal static bool IsIdentifier(string? text) =>
            HasIdentifierShape(text) && !s_keywords.Contains(text!);

        /// <summary>
        /// Whether <paramref name="text"/> is usable as a namespace: one or
        /// more dot-separated identifiers, each of which
        /// <see cref="IsIdentifier"/> accepts.
        /// </summary>
        internal static bool IsNamespace(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            var segments = text.Split('.');
            for (var i = 0; i < segments.Length; i++)
            {
                if (!IsIdentifier(segments[i]))
                {
                    return false;
                }
            }
            return true;
        }

        // The character-shape half only — what the port Pattern keyword
        // also enforces at D3.
        private static bool HasIdentifierShape(string? text) =>
            !string.IsNullOrEmpty(text) && s_shape.IsMatch(text);
    }
}
