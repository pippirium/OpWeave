#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core
{
    /// <summary>
    /// OpWeave-owned strict JSON text reader for Canonical JSON v1 (SPEC §0.3, §0.6
    /// SG-02). Duplicate keys, comments, leading zeros, <c>+</c> signs, lone surrogates,
    /// unescaped control characters and trailing content are detected in the text itself,
    /// never left to Newtonsoft defaults. The reader is iterative, so nesting depth is
    /// bounded by input size rather than the thread stack.
    /// </summary>
    /// <remarks>
    /// Failures are <see cref="Result{T}"/>s with a single internal diagnostic: an empty
    /// <see cref="Diagnostic.Code"/> (not a published OW-* code), a payload-free fixed
    /// English reason carrying the character offset, and a JsonPath for the enclosing
    /// value in which every object member step is the <c>*</c> wildcard (array indices
    /// stay literal). The owning layer maps the diagnostic to OW-WF-001 (workflow
    /// reading), OW-STORE-002 (Run Store files) or OW-CFG-001 (settings) exactly once
    /// before publishing; decoded keys and values are source JSON that may contain
    /// secrets and are never copied into a diagnostic.
    /// </remarks>
    internal static class CanonicalJsonReader
    {
        public static Result<JToken> Parse(string text)
        {
            if (text is null)
            {
                throw new ArgumentNullException(nameof(text));
            }
            return new Reader(text).Run();
        }

        private sealed class Frame
        {
            public Frame(JContainer container, string path, bool isObject)
            {
                Container = container;
                Path = path;
                IsObject = isObject;
            }

            public JContainer Container { get; }
            public string Path { get; }
            public bool IsObject { get; }
            public HashSet<string> Keys { get; } = new HashSet<string>(StringComparer.Ordinal);
            public string? PendingKey { get; set; }
            public int NextIndex { get; set; }
        }

        private sealed class Reader
        {
            private readonly string _text;
            private readonly List<Frame> _stack = new List<Frame>();
            private Diagnostic? _failure;
            private JToken? _root;
            private int _pos;

            public Reader(string text)
            {
                _text = text;
            }

            public Result<JToken> Run()
            {
                var state = State.Value;
                JToken? completed = null;
                while (state != State.Done)
                {
                    switch (state)
                    {
                        case State.Value:
                        {
                            var next = ReadValueOrOpen(out completed);
                            if (next is null)
                            {
                                state = AttachOrFinish(completed!);
                            }
                            else
                            {
                                state = next.Value;
                            }
                            break;
                        }
                        case State.ObjectKeyOrEnd:
                        case State.ObjectKey:
                        {
                            state = ReadObjectKey(state == State.ObjectKeyOrEnd, ref completed);
                            break;
                        }
                        case State.ObjectEnd:
                        {
                            state = ReadObjectEnd(ref completed);
                            break;
                        }
                        case State.ArrayValueOrEnd:
                        {
                            SkipWhitespace();
                            if (Eof())
                            {
                                return Fail("Unexpected end of JSON text", _pos, CurrentPath());
                            }
                            if (_text[_pos] == ']')
                            {
                                _pos++;
                                completed = Pop();
                                state = AttachOrFinish(completed);
                            }
                            else
                            {
                                state = State.Value;
                            }
                            break;
                        }
                        case State.ArrayEnd:
                        {
                            state = ReadArrayEnd(ref completed);
                            break;
                        }
                    }
                    if (_failure is not null)
                    {
                        return Result<JToken>.Failure(_failure);
                    }
                }
                SkipWhitespace();
                if (!Eof())
                {
                    return Fail("Unexpected content after the JSON value", _pos, "$");
                }
                return Result<JToken>.Success(_root!);
            }

            private enum State
            {
                Value,          // expecting a JSON value
                ObjectKeyOrEnd, // just after '{': a key string or '}'
                ObjectKey,      // just after ',' in an object: a key string
                ObjectEnd,      // after a member value: ',' or '}'
                ArrayValueOrEnd,// just after '[': a value or ']'
                ArrayEnd,       // after an item: ',' or ']'
                Done,
            }

            // Returns null when a scalar was read into `completed`; otherwise the state the
            // main loop should enter (after '{', '[', or a key's ':'). On failure records
            // _failure and returns Done.
            private State? ReadValueOrOpen(out JToken? completed)
            {
                SkipWhitespace();
                if (Eof())
                {
                    _failure = Reason("Unexpected end of JSON text", _pos, CurrentPath());
                    completed = null;
                    return State.Done;
                }
                var c = _text[_pos];
                switch (c)
                {
                    case '{':
                        _pos++;
                        _stack.Add(new Frame(new JObject(), CurrentValuePath(), true));
                        completed = null;
                        return State.ObjectKeyOrEnd;
                    case '[':
                        _pos++;
                        _stack.Add(new Frame(new JArray(), CurrentValuePath(), false));
                        completed = null;
                        return State.ArrayValueOrEnd;
                    case '"':
                    {
                        var text = ReadString();
                        if (_failure is not null)
                        {
                            completed = null;
                            return State.Done;
                        }
                        completed = new JValue(text);
                        return null;
                    }
                    case 't':
                        completed = MatchLiteral("true") ? new JValue(true) : null;
                        return completed is null && _failure is not null ? State.Done : null;
                    case 'f':
                        completed = MatchLiteral("false") ? new JValue(false) : null;
                        return completed is null && _failure is not null ? State.Done : null;
                    case 'n':
                        completed = MatchLiteral("null") ? JValue.CreateNull() : null;
                        return completed is null && _failure is not null ? State.Done : null;
                    default:
                        if (c == '-' || IsDigit(c))
                        {
                            completed = ReadNumber();
                            return _failure is not null ? State.Done : (State?)null;
                        }
                        _failure = Reason("Invalid JSON syntax", _pos, CurrentPath());
                        completed = null;
                        return State.Done;
                }
            }

            private State AttachOrFinish(JToken value)
            {
                if (_stack.Count == 0)
                {
                    _root = value;
                    return State.Done;
                }
                var frame = Top();
                if (frame.IsObject)
                {
                    frame.Container.Add(new JProperty(frame.PendingKey!, value));
                    frame.PendingKey = null;
                    return State.ObjectEnd;
                }
                frame.Container.Add(value);
                frame.NextIndex++;
                return State.ArrayEnd;
            }

            private State ReadObjectKey(bool allowEnd, ref JToken? completed)
            {
                SkipWhitespace();
                if (Eof())
                {
                    _failure = Reason("Unexpected end of JSON text", _pos, Top().Path);
                    return State.Done;
                }
                var c = _text[_pos];
                if (c == '}')
                {
                    if (!allowEnd)
                    {
                        _failure = Reason("Invalid JSON syntax", _pos, Top().Path);
                        return State.Done;
                    }
                    _pos++;
                    completed = Pop();
                    return AttachOrFinish(completed);
                }
                if (c != '"')
                {
                    _failure = Reason("Invalid JSON syntax", _pos, Top().Path);
                    return State.Done;
                }
                var key = ReadString();
                if (_failure is not null)
                {
                    return State.Done;
                }
                var frame = Top();
                var memberPath = frame.Path + MemberStep;
                if (!frame.Keys.Add(key))
                {
                    _failure = Reason("Duplicate object key", _pos, memberPath);
                    return State.Done;
                }
                SkipWhitespace();
                if (Eof() || _text[_pos] != ':')
                {
                    _failure = Reason("Invalid JSON syntax", _pos, memberPath);
                    return State.Done;
                }
                _pos++;
                frame.PendingKey = key;
                return State.Value;
            }

            private State ReadObjectEnd(ref JToken? completed)
            {
                SkipWhitespace();
                if (Eof())
                {
                    _failure = Reason("Unexpected end of JSON text", _pos, Top().Path);
                    return State.Done;
                }
                var c = _text[_pos];
                if (c == ',')
                {
                    _pos++;
                    return State.ObjectKey;
                }
                if (c == '}')
                {
                    _pos++;
                    completed = Pop();
                    return AttachOrFinish(completed);
                }
                _failure = Reason("Invalid JSON syntax", _pos, Top().Path);
                return State.Done;
            }

            private State ReadArrayEnd(ref JToken? completed)
            {
                SkipWhitespace();
                if (Eof())
                {
                    _failure = Reason("Unexpected end of JSON text", _pos, Top().Path);
                    return State.Done;
                }
                var c = _text[_pos];
                if (c == ',')
                {
                    _pos++;
                    return State.Value;
                }
                if (c == ']')
                {
                    _pos++;
                    completed = Pop();
                    return AttachOrFinish(completed);
                }
                _failure = Reason("Invalid JSON syntax", _pos, Top().Path);
                return State.Done;
            }

            // Reads a JSON string body starting at the opening quote; returns the decoded
            // text. Raw control characters, invalid escapes and unpaired surrogates — raw
            // or \uXXXX-encoded — are rejected (SPEC §0.3 item 2, CJ-06).
            private string ReadString()
            {
                var start = _pos;
                _pos++; // opening quote
                var builder = new StringBuilder();
                while (_pos < _text.Length)
                {
                    var c = _text[_pos];
                    if (c == '"')
                    {
                        _pos++;
                        return builder.ToString();
                    }
                    if (c == '\\')
                    {
                        _pos++;
                        if (Eof())
                        {
                            break;
                        }
                        var e = _text[_pos];
                        _pos++;
                        switch (e)
                        {
                            case '"': builder.Append('"'); continue;
                            case '\\': builder.Append('\\'); continue;
                            case '/': builder.Append('/'); continue;
                            case 'b': builder.Append('\b'); continue;
                            case 'f': builder.Append('\f'); continue;
                            case 'n': builder.Append('\n'); continue;
                            case 'r': builder.Append('\r'); continue;
                            case 't': builder.Append('\t'); continue;
                            case 'u':
                            {
                                var hi = ReadHex4(_pos - 1);
                                if (_failure is not null)
                                {
                                    return string.Empty;
                                }
                                if (hi >= 0xD800 && hi <= 0xDBFF)
                                {
                                    if (_pos + 1 >= _text.Length ||
                                        _text[_pos] != '\\' || _text[_pos + 1] != 'u')
                                    {
                                        _failure = Reason(
                                            "JSON string contains an unpaired surrogate",
                                            _pos - 6, ValuePath());
                                        return string.Empty;
                                    }
                                    _pos += 2;
                                    var lo = ReadHex4(_pos - 1);
                                    if (_failure is not null)
                                    {
                                        return string.Empty;
                                    }
                                    if (lo < 0xDC00 || lo > 0xDFFF)
                                    {
                                        _failure = Reason(
                                            "JSON string contains an unpaired surrogate",
                                            _pos - 6, ValuePath());
                                        return string.Empty;
                                    }
                                    builder.Append((char)hi);
                                    builder.Append((char)lo);
                                    continue;
                                }
                                if (hi >= 0xDC00 && hi <= 0xDFFF)
                                {
                                    _failure = Reason(
                                        "JSON string contains an unpaired surrogate",
                                        _pos - 6, ValuePath());
                                    return string.Empty;
                                }
                                builder.Append((char)hi);
                                continue;
                            }
                            default:
                                _failure = Reason(
                                    "JSON string contains an invalid escape sequence",
                                    _pos - 2, ValuePath());
                                return string.Empty;
                        }
                    }
                    if (c < ' ')
                    {
                        _failure = Reason(
                            "JSON string contains an unescaped control character", _pos, ValuePath());
                        return string.Empty;
                    }
                    if (char.IsHighSurrogate(c))
                    {
                        if (_pos + 1 >= _text.Length || !char.IsLowSurrogate(_text[_pos + 1]))
                        {
                            _failure = Reason(
                                "JSON string contains an unpaired surrogate", _pos, ValuePath());
                            return string.Empty;
                        }
                        builder.Append(c);
                        builder.Append(_text[_pos + 1]);
                        _pos += 2;
                        continue;
                    }
                    if (char.IsLowSurrogate(c))
                    {
                        _failure = Reason(
                            "JSON string contains an unpaired surrogate", _pos, ValuePath());
                        return string.Empty;
                    }
                    builder.Append(c);
                    _pos++;
                }
                _failure = Reason("JSON string is not terminated", start, ValuePath());
                return string.Empty;
            }

            private int ReadHex4(int errorOffset)
            {
                if (_pos + 4 > _text.Length)
                {
                    _failure = Reason(
                        "JSON string contains an invalid escape sequence", errorOffset, ValuePath());
                    return 0;
                }
                var value = 0;
                for (var i = 0; i < 4; i++)
                {
                    var digit = HexDigit(_text[_pos + i]);
                    if (digit < 0)
                    {
                        _failure = Reason(
                            "JSON string contains an invalid escape sequence", errorOffset, ValuePath());
                        return 0;
                    }
                    value = value * 16 + digit;
                }
                _pos += 4;
                return value;
            }

            private static int HexDigit(char c)
            {
                if (c >= '0' && c <= '9') return c - '0';
                if (c >= 'a' && c <= 'f') return c - 'a' + 10;
                if (c >= 'A' && c <= 'F') return c - 'A' + 10;
                return -1;
            }

            // Scans the raw number token and returns it as a typed JValue: Int64 tokens
            // (no fraction or exponent) stay Int64 within range, fraction/exponent tokens
            // become finite binary64 (CanonicalJsonNumber, SPEC §0.3 items 5-6).
            private JToken? ReadNumber()
            {
                var start = _pos;
                while (_pos < _text.Length)
                {
                    var c = _text[_pos];
                    if (IsDigit(c) || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-')
                    {
                        _pos++;
                        continue;
                    }
                    break;
                }
                var token = _text.Substring(start, _pos - start);
                var isDouble = token.IndexOf('.') >= 0 || token.IndexOf('e') >= 0 ||
                               token.IndexOf('E') >= 0;
                var path = ValuePath();
                if (isDouble)
                {
                    var parsed = CanonicalJsonNumber.ParseDouble(token);
                    if (!parsed.Ok)
                    {
                        _failure = Located(parsed.Diagnostics[0], start, path);
                        return null;
                    }
                    return new JValue(parsed.Value);
                }
                var integer = CanonicalJsonNumber.ParseInt64(token);
                if (!integer.Ok)
                {
                    _failure = Located(integer.Diagnostics[0], start, path);
                    return null;
                }
                return new JValue(integer.Value);
            }

            private bool MatchLiteral(string literal)
            {
                for (var i = 0; i < literal.Length; i++)
                {
                    if (_pos + i >= _text.Length || _text[_pos + i] != literal[i])
                    {
                        _failure = Reason("Invalid JSON literal", _pos, CurrentPath());
                        return false;
                    }
                }
                _pos += literal.Length;
                return true;
            }

            private void SkipWhitespace()
            {
                while (_pos < _text.Length)
                {
                    var c = _text[_pos];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                    {
                        _pos++;
                        continue;
                    }
                    break;
                }
            }

            private bool Eof() => _pos >= _text.Length;

            private Frame Top() => _stack[_stack.Count - 1];

            private JToken Pop()
            {
                var frame = Top();
                _stack.RemoveAt(_stack.Count - 1);
                return frame.Container;
            }

            // Path of the enclosing container ("$" for the root).
            private string CurrentPath() => _stack.Count == 0 ? "$" : Top().Path;

            // Path of the value currently being read.
            private string CurrentValuePath()
            {
                if (_stack.Count == 0)
                {
                    return "$";
                }
                var frame = Top();
                return frame.IsObject
                    ? frame.Path + (frame.PendingKey is null ? string.Empty : MemberStep)
                    : frame.Path + "[" + frame.NextIndex.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) + "]";
            }

            private string ValuePath() => CurrentValuePath();

            // SG-02: every object member step is rendered as the "*" wildcard so decoded
            // key text — source JSON that may carry secrets — never reaches a diagnostic.
            // Array indices stay literal (e.g. "$.*[2].*").
            private const string MemberStep = ".*";

            private Result<JToken> Fail(string reason, int offset, string path)
            {
                _failure = Reason(reason, offset, path);
                return Result<JToken>.Failure(_failure);
            }

            private static Diagnostic Reason(string reason, int offset, string path) =>
                new Diagnostic(
                    string.Empty,
                    Severity.Error,
                    reason + " at offset " +
                        offset.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".",
                    JsonPath: path);

            // Rewraps a nested SG-02 diagnostic (e.g. from CanonicalJsonNumber) with the
            // reader's offset and JsonPath; the token text is never echoed (no payload).
            private static Diagnostic Located(Diagnostic inner, int offset, string path) =>
                new Diagnostic(
                    string.Empty,
                    inner.Severity,
                    inner.Message.TrimEnd('.') + " at offset " +
                        offset.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".",
                    JsonPath: path);

            private static bool IsDigit(char c) => c >= '0' && c <= '9';
        }
    }
}
