#nullable enable

using System;
using System.Collections.Generic;
using System.Text;

namespace OpWeave.Core
{
    /// <summary>
    /// A parsed Type ID (SPEC §B1): the dotted <see cref="Name"/> plus zero or more generic
    /// <see cref="Args"/>. <see cref="Parse"/> validates the grammar and
    /// <see cref="ToString"/> prints the canonical form, which never contains whitespace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Grammar (SPEC §B1):
    /// <code>
    /// TypeId  := Name [ "&lt;" TypeArg { "," TypeArg } "&gt;" ]
    /// TypeArg := TypeId | "type:" DotNetName      ; e.g. type:MyGame.PlayerController
    /// Name    := Ident "." Ident { "." Ident }    ; e.g. unity.GameObjectRef
    /// Ident   := [A-Za-z][A-Za-z0-9_]*
    /// </code>
    /// A <c>type:</c> argument names a Unity class by its fully qualified name and is
    /// represented as a leaf <see cref="TypeRef"/> whose <see cref="Name"/> carries the
    /// <c>type:</c> prefix (e.g. <c>type:MyGame.PlayerController</c>); it never has
    /// <see cref="Args"/> of its own and may appear only inside a generic argument list.
    /// </para>
    /// <para>
    /// Local choices where the SPEC grammar is silent: <c>DotNetName</c> accepts one or
    /// more dot-separated identifiers (a class in the global namespace has no dot);
    /// whitespace is tolerated between tokens and dropped by the canonical form; generic
    /// nesting is bounded at <see cref="MaxGenericNesting"/> levels so hostile input
    /// cannot exhaust the stack; and an empty argument list <c>&lt;&gt;</c> is the only
    /// arity fault detectable without a registry — OW-TYPE-005 — while every other
    /// malformation is OW-TYPE-001 (SPEC §J). Namespace reservation
    /// (<c>core.*</c> / <c>unity.*</c> / <c>opweave.*</c>, <c>proj.</c> for project types)
    /// is enforced where definitions are registered, not by this parser.
    /// </para>
    /// <para>
    /// Equality is by value: the <see cref="Name"/> compared Ordinal and the
    /// <see cref="Args"/> element-wise, so two parses of the same canonical string are
    /// equal. <see cref="Args"/> is stored as an immutable defensive copy; the constructor
    /// enforces only the structural invariants needed for a round-trippable shape
    /// (non-empty name, non-null leaf-preserving arguments, no arguments on a
    /// <c>type:</c> class-id leaf) — full grammar validation is <see cref="Parse"/>'s job.
    /// </para>
    /// </remarks>
    public sealed record TypeRef(string Name, IReadOnlyList<TypeRef> Args)
    {
        /// <summary>
        /// The <c>type:</c> prefix marking a fully qualified class-name argument (SPEC §B1).
        /// </summary>
        internal const string ClassRefPrefix = "type:";

        /// <summary>
        /// The maximum generic nesting depth <see cref="Parse"/> accepts; deeper input
        /// fails with OW-TYPE-001.
        /// </summary>
        internal const int MaxGenericNesting = 64;

        private static readonly IReadOnlyList<TypeRef> s_noArgs =
            Array.AsReadOnly(Array.Empty<TypeRef>());

        /// <summary>
        /// The dotted Type ID name (e.g. <c>unity.ComponentRef</c>), or
        /// <c>"type:" + fully qualified class name</c> for a class-id argument leaf.
        /// </summary>
        public string Name { get; init; } =
            string.IsNullOrEmpty(Name)
                ? throw new ArgumentException(
                    "Type ID name must not be null or empty.", nameof(Name))
                : Name;

        /// <summary>The generic arguments; always empty for a <c>type:</c> class-id leaf.</summary>
        public IReadOnlyList<TypeRef> Args { get; init; } = CheckArgs(Name, Args);

        /// <summary>
        /// Parses a Type ID string (SPEC §B1). Whitespace between tokens is accepted and
        /// dropped by the canonical form. A malformed input fails with OW-TYPE-001 and an
        /// empty generic argument list <c>&lt;&gt;</c> with OW-TYPE-005; the first fault
        /// found is reported as a single diagnostic.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="id"/> is null.</exception>
        public static Result<TypeRef> Parse(string id)
        {
            if (id is null)
            {
                throw new ArgumentNullException(nameof(id));
            }
            var scanner = new Scanner(id);
            var type = scanner.ParseAll();
            return type is null
                ? Result<TypeRef>.Failure(scanner.Error!)
                : Result<TypeRef>.Success(type);
        }

        /// <summary>
        /// The canonical Type ID string: <see cref="Name"/> followed by
        /// <c>&lt;arg,…&gt;</c> when there are <see cref="Args"/>, never any whitespace
        /// (SPEC §B1).
        /// </summary>
        public override string ToString()
        {
            var builder = new StringBuilder();
            AppendTo(builder);
            return builder.ToString();
        }

        /// <summary>
        /// Value equality: <see cref="Name"/> Ordinal plus <see cref="Args"/> element-wise.
        /// </summary>
        public bool Equals(TypeRef? other)
        {
            if (other is null || !string.Equals(Name, other.Name, StringComparison.Ordinal))
            {
                return false;
            }
            if (Args.Count != other.Args.Count)
            {
                return false;
            }
            for (var i = 0; i < Args.Count; i++)
            {
                if (!Args[i].Equals(other.Args[i]))
                {
                    return false;
                }
            }
            return true;
        }

        public override int GetHashCode()
        {
            var hash = StringComparer.Ordinal.GetHashCode(Name);
            unchecked
            {
                for (var i = 0; i < Args.Count; i++)
                {
                    hash = (hash * 397) ^ Args[i].GetHashCode();
                }
            }
            return hash;
        }

        private void AppendTo(StringBuilder builder)
        {
            builder.Append(Name);
            if (Args.Count == 0)
            {
                return;
            }
            builder.Append('<');
            for (var i = 0; i < Args.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }
                Args[i].AppendTo(builder);
            }
            builder.Append('>');
        }

        private static IReadOnlyList<TypeRef> CheckArgs(string name, IReadOnlyList<TypeRef> args)
        {
            var copy = ReadOnlyCollections.CopyList(args);
            if (name.StartsWith(ClassRefPrefix, StringComparison.Ordinal) && copy.Count != 0)
            {
                throw new ArgumentException(
                    "A 'type:' class-id reference is a leaf and cannot have generic arguments.",
                    nameof(args));
            }
            for (var i = 0; i < copy.Count; i++)
            {
                if (copy[i] is null)
                {
                    throw new ArgumentException(
                        "Generic arguments must not contain null.", nameof(args));
                }
            }
            return copy;
        }

        // ── Recursive-descent scanner for the SPEC §B1 grammar ──────────────────
        // Whitespace is skipped before every token, so it may appear at any token
        // boundary and is dropped entirely by the canonical form. The first fault
        // wins: syntax errors produce OW-TYPE-001, an empty "<>" OW-TYPE-005.

        private sealed class Scanner
        {
            private readonly string _text;
            private int _pos;

            internal Scanner(string text) => _text = text;

            internal Diagnostic? Error { get; private set; }

            internal TypeRef? ParseAll()
            {
                var type = ParseTypeId(0);
                if (Error is not null)
                {
                    return null;
                }
                SkipWhitespace();
                if (_pos != _text.Length)
                {
                    Fail(_pos, "the end of the Type ID");
                    return null;
                }
                return type;
            }

            private TypeRef? ParseTypeId(int depth)
            {
                if (depth > MaxGenericNesting)
                {
                    Fail(_pos, $"at most {MaxGenericNesting} levels of nested generic arguments");
                    return null;
                }
                var name = ParseDottedName(requireDot: true);
                if (name is null)
                {
                    return null;
                }
                if (Peek() != '<')
                {
                    return new TypeRef(name, s_noArgs);
                }
                var openOffset = _pos;
                _pos++;
                if (Peek() == '>')
                {
                    FailEmptyArgs(openOffset);
                    return null;
                }
                var args = new List<TypeRef>();
                while (true)
                {
                    var arg = ParseTypeArg(depth + 1);
                    if (arg is null)
                    {
                        return null;
                    }
                    args.Add(arg);
                    var next = Peek();
                    if (next == ',')
                    {
                        _pos++;
                        continue;
                    }
                    if (next == '>')
                    {
                        _pos++;
                        break;
                    }
                    Fail(_pos, "',' or '>'");
                    return null;
                }
                return new TypeRef(name, args);
            }

            private TypeRef? ParseTypeArg(int depth)
            {
                SkipWhitespace();
                if (Matches(ClassRefPrefix))
                {
                    _pos += ClassRefPrefix.Length;
                    var className = ParseDottedName(requireDot: false);
                    return className is null
                        ? null
                        : new TypeRef(ClassRefPrefix + className, s_noArgs);
                }
                return ParseTypeId(depth);
            }

            private string? ParseDottedName(bool requireDot)
            {
                var builder = new StringBuilder();
                var segments = 0;
                while (true)
                {
                    var ident = ScanIdent(requireDot ? "an identifier" : "a fully qualified class name");
                    if (ident is null)
                    {
                        return null;
                    }
                    builder.Append(ident);
                    segments++;
                    if (Peek() != '.')
                    {
                        break;
                    }
                    _pos++;
                    builder.Append('.');
                }
                if (requireDot && segments == 1)
                {
                    Fail(_pos, "a dotted name like 'unity.GameObjectRef'");
                    return null;
                }
                return builder.ToString();
            }

            private string? ScanIdent(string expected)
            {
                SkipWhitespace();
                if (_pos >= _text.Length || !IsIdentStart(_text[_pos]))
                {
                    Fail(_pos, expected);
                    return null;
                }
                var start = _pos++;
                while (_pos < _text.Length && IsIdentPart(_text[_pos]))
                {
                    _pos++;
                }
                return _text.Substring(start, _pos - start);
            }

            private char? Peek()
            {
                SkipWhitespace();
                return _pos < _text.Length ? _text[_pos] : (char?)null;
            }

            private bool Matches(string literal)
            {
                for (var i = 0; i < literal.Length; i++)
                {
                    if (_pos + i >= _text.Length || _text[_pos + i] != literal[i])
                    {
                        return false;
                    }
                }
                return true;
            }

            private void SkipWhitespace()
            {
                while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
                {
                    _pos++;
                }
            }

            private void Fail(int offset, string expected) =>
                Error ??= ErrorCodes.Create(
                    ErrorCodes.OW_TYPE_001,
                    $"Malformed Type ID '{_text}'.",
                    hint: $"Expected {expected} at offset {offset}.");

            private void FailEmptyArgs(int openOffset) =>
                Error ??= ErrorCodes.Create(
                    ErrorCodes.OW_TYPE_005,
                    $"Malformed Type ID '{_text}'.",
                    hint: $"Expected at least one generic argument after '<' at offset {openOffset}.");

            private static bool IsIdentStart(char c) =>
                (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

            private static bool IsIdentPart(char c) =>
                IsIdentStart(c) || (c >= '0' && c <= '9') || c == '_';
        }
    }
}
