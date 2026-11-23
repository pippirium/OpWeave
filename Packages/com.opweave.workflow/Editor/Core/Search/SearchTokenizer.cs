#nullable enable

using System;
using System.Collections.Generic;
using System.Text;

namespace OpWeave.Core.Search
{
    /// <summary>
    /// The shared SPEC §C5 normalization and tokenization of the Operation
    /// search index: full-width alphanumerics fold to half-width, the katakana
    /// long-vowel mark drops so its presence is never distinguished
    /// (「プレハブ」と「プレハブー」index identically), lowercase
    /// ASCII-alphanumeric runs become word tokens and every non-alphanumeric,
    /// non-whitespace run becomes character bigrams. Queries are tokenized by
    /// the identical rules, so indexed text and query text always meet in the
    /// same token space.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An Operation ID is split on <c>.</c> and <c>_</c> like any other
    /// non-alphanumeric boundary and contributes word tokens only
    /// (<see cref="TokenizeIdentifier"/>); the punctuation segments of an ID
    /// are separators, never index terms.
    /// </para>
    /// <para>
    /// Whitespace is a token boundary, never an indexed character: it joins
    /// no run and forms no token, so a multiword query carries only its
    /// words and a whitespace-only query has no tokens at all.
    /// </para>
    /// <para>
    /// A one-character non-alphanumeric run cannot form a bigram and is kept
    /// verbatim as a degenerate token, so no indexed character can silently
    /// disappear from the token space.
    /// </para>
    /// </remarks>
    public static class SearchTokenizer
    {
        /// <summary>
        /// Applies the SPEC §C5 normalization: full-width ASCII variants
        /// (U+FF01–U+FF5E) fold to their ASCII forms, the ideographic space
        /// (U+3000) folds to a plain space and the katakana long-vowel mark
        /// (U+30FC) is dropped.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
        public static string Normalize(string text)
        {
            if (text is null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            StringBuilder? builder = null;
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch >= '！' && ch <= '｝')
                {
                    // U+FF01..U+FF5E fold onto ASCII 0x21..0x7E.
                    builder ??= NewBuilder(text, i);
                    builder.Append((char)(ch - 0xFEE0));
                }
                else if (ch == '　')
                {
                    // U+3000 ideographic space folds to a plain space.
                    builder ??= NewBuilder(text, i);
                    builder.Append(' ');
                }
                else if (ch == 'ー')
                {
                    // U+30FC katakana long-vowel mark is dropped.
                    builder ??= NewBuilder(text, i);
                }
                else
                {
                    builder?.Append(ch);
                }
            }
            return builder?.ToString() ?? text;
        }

        /// <summary>
        /// The distinct §C5 tokens of a text field: lowercased word tokens for
        /// ASCII-alphanumeric runs, character bigrams for the remaining
        /// non-whitespace runs (a one-character run contributes itself).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
        public static IReadOnlyList<string> Tokenize(string text)
        {
            if (text is null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            var tokens = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var run in SplitRuns(Normalize(text)))
            {
                if (run.IsWord)
                {
                    Add(seen, tokens, run.Text.ToLowerInvariant());
                }
                else if (run.Text.Length == 1)
                {
                    Add(seen, tokens, run.Text);
                }
                else
                {
                    for (var i = 0; i + 1 < run.Text.Length; i++)
                    {
                        Add(seen, tokens, run.Text.Substring(i, 2));
                    }
                }
            }
            return tokens;
        }

        /// <summary>
        /// The distinct §C5 word tokens of an Operation ID: it splits on
        /// <c>.</c> and <c>_</c> — and on every other non-alphanumeric
        /// character — and contributes lowercased word tokens only.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="identifier"/> is null.</exception>
        public static IReadOnlyList<string> TokenizeIdentifier(string identifier)
        {
            if (identifier is null)
            {
                throw new ArgumentNullException(nameof(identifier));
            }

            var tokens = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var run in SplitRuns(Normalize(identifier)))
            {
                if (run.IsWord)
                {
                    Add(seen, tokens, run.Text.ToLowerInvariant());
                }
            }
            return tokens;
        }

        /// <summary>
        /// True when <paramref name="token"/> is a word token — every
        /// character an ASCII letter or digit. Word tokens match by prefix;
        /// any other token (a bigram or a lone non-alphanumeric character)
        /// matches only by exact equality.
        /// </summary>
        internal static bool IsWordToken(string token)
        {
            for (var i = 0; i < token.Length; i++)
            {
                if (!IsAsciiAlphanumeric(token[i]))
                {
                    return false;
                }
            }
            return token.Length != 0;
        }

        private static void Add(HashSet<string> seen, List<string> tokens, string token)
        {
            if (seen.Add(token))
            {
                tokens.Add(token);
            }
        }

        private static StringBuilder NewBuilder(string text, int index)
        {
            var builder = new StringBuilder(text.Length);
            builder.Append(text, 0, index);
            return builder;
        }

        private static bool IsAsciiAlphanumeric(char ch) =>
            (ch >= '0' && ch <= '9') ||
            (ch >= 'A' && ch <= 'Z') ||
            (ch >= 'a' && ch <= 'z');

        private readonly struct Run
        {
            internal Run(bool isWord, string text)
            {
                IsWord = isWord;
                Text = text;
            }

            internal bool IsWord { get; }

            internal string Text { get; }
        }

        private static List<Run> SplitRuns(string text)
        {
            var runs = new List<Run>();
            var start = -1;
            var word = false;
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    // Whitespace is a token boundary, never indexed text: it
                    // closes the current run and contributes no run of its own.
                    if (start >= 0)
                    {
                        runs.Add(new Run(word, text.Substring(start, i - start)));
                        start = -1;
                    }
                    continue;
                }
                var isWord = IsAsciiAlphanumeric(text[i]);
                if (start < 0)
                {
                    start = i;
                    word = isWord;
                }
                else if (isWord != word)
                {
                    runs.Add(new Run(word, text.Substring(start, i - start)));
                    start = i;
                    word = isWord;
                }
            }
            if (start >= 0)
            {
                runs.Add(new Run(word, text.Substring(start)));
            }
            return runs;
        }
    }
}
