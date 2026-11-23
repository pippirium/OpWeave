#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;

namespace OpWeave.Core.Search
{
    /// <summary>
    /// The immutable SPEC §C5 Operation search index built once from one
    /// <see cref="RegistrySnapshot"/> and exposed to consumers through
    /// <see cref="RegistrySnapshot.SearchIndex"/> — every published snapshot
    /// carries its own index, so an index can never retain Operations the
    /// current Registry no longer contains. Custom Operations are indexed the
    /// moment they register because they are ordinary snapshot members.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The indexed fields of each Descriptor are exactly the §C5 set: the ID
    /// (split on <c>.</c> and <c>_</c>), DisplayName, Summary, Tags and
    /// Keywords plus every Localized language's DisplayName, Summary and
    /// Keywords. All fields share the <see cref="SearchTokenizer"/> token
    /// space — lowercase word tokens for ASCII-alphanumeric runs, character
    /// bigrams otherwise — and a query is tokenized by the identical rules.
    /// </para>
    /// <para>
    /// Ranking is a local, reversible implementation choice the §C5 tests
    /// pin only observably (「プレハブ 作成」must place
    /// <c>unity.prefab.create_from_gameobject</c> in the top three; the
    /// transform-reset pair must lead): a candidate scores the best Dice
    /// coefficient 2·|Q∩F|/(|Q|+|F|) over its indexed fields, where a word
    /// token of the query matches a field word token by prefix and a bigram
    /// token matches by membership; |Q∩F| counts each query token and each
    /// field token at most once so the score stays inside [0, 1]; an exact
    /// normalized-ID match scores 1.0.
    /// Ties order by ID Ordinal then version — the deterministic §C6.3
    /// tie-break the search services built on this index also apply. The
    /// service-level §C6 score formula is a separate contract layered over
    /// this index by its own ticket.
    /// </para>
    /// <para>
    /// Type search keeps no index at all (SPEC §C5.4): callers scan every
    /// Operation's ports and judge assignability through B6
    /// (<see cref="OpWeave.Core.Types.TypeAssignability"/>) against the
    /// snapshot, so this type indexes descriptor text only — never ports or
    /// Type IDs.
    /// </para>
    /// </remarks>
    public sealed class OperationSearchIndex
    {
        // The §C5 indexed field set per Descriptor.
        private const int FieldId = 0;
        private const int FieldDisplayName = 1;
        private const int FieldSummary = 2;
        private const int FieldTags = 3;
        private const int FieldKeywords = 4;
        private const int FirstLocalizedField = 5;

        private readonly RegisteredOperation[] _operations;

        // _fields[d][f] is the token set of descriptor d's indexed field f;
        // _fieldWords[d][f] is the same set filtered to word tokens for the
        // prefix-match scan.
        private readonly HashSet<string>[][] _fields;
        private readonly List<string>[][] _fieldWords;
        private readonly string[] _normalizedIds;

        // Inverted postings: token -> snapshot member indices (ascending).
        private readonly Dictionary<string, List<int>> _wordPostings =
            new Dictionary<string, List<int>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<int>> _bigramPostings =
            new Dictionary<string, List<int>>(StringComparer.Ordinal);

        private OperationSearchIndex(
            RegisteredOperation[] operations,
            HashSet<string>[][] fields,
            List<string>[][] fieldWords,
            string[] normalizedIds)
        {
            _operations = operations;
            _fields = fields;
            _fieldWords = fieldWords;
            _normalizedIds = normalizedIds;
        }

        /// <summary>The number of registered Operations this index covers.</summary>
        public int OperationCount => _operations.Length;

        /// <summary>
        /// Rebuilds the index from <paramref name="snapshot"/>: every
        /// registered Operation of the snapshot contributes its §C5 fields
        /// and the postings are rebuilt in full, so a new snapshot's index
        /// holds no member of a previous one.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
        public static OperationSearchIndex Build(RegistrySnapshot snapshot)
        {
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            var operations = snapshot.Operations;
            var count = operations.Count;
            var members = new RegisteredOperation[count];
            var fields = new HashSet<string>[count][];
            var fieldWords = new List<string>[count][];
            var normalizedIds = new string[count];
            var index = new OperationSearchIndex(members, fields, fieldWords, normalizedIds);

            for (var d = 0; d < count; d++)
            {
                var operation = operations[d];
                members[d] = operation;
                var descriptor = operation.Descriptor;
                normalizedIds[d] = SearchTokenizer.Normalize(descriptor.Id);
                var built = BuildFields(descriptor);
                fields[d] = built.Sets;
                fieldWords[d] = built.Words;
                for (var f = 0; f < built.Sets.Length; f++)
                {
                    foreach (var token in built.Sets[f])
                    {
                        var postings = SearchTokenizer.IsWordToken(token)
                            ? index._wordPostings
                            : index._bigramPostings;
                        if (!postings.TryGetValue(token, out var docs))
                        {
                            docs = new List<int>();
                            postings.Add(token, docs);
                        }
                        docs.Add(d);
                    }
                }
            }
            return index;
        }

        /// <summary>
        /// Tokenizes <paramref name="query"/> by the §C5 rules and returns up
        /// to <paramref name="limit"/> hits ordered by score descending then
        /// ID Ordinal then version — deterministic for a fixed index and
        /// query. An empty query (no tokens) returns no hits.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="query"/> is null.</exception>
        public IReadOnlyList<OperationSearchHit> Search(string query, int limit = 10)
        {
            if (query is null)
            {
                throw new ArgumentNullException(nameof(query));
            }

            if (limit <= 0)
            {
                return Array.Empty<OperationSearchHit>();
            }

            var queryTokens = SearchTokenizer.Tokenize(query);
            if (queryTokens.Count == 0)
            {
                return Array.Empty<OperationSearchHit>();
            }

            var normalizedQuery = SearchTokenizer.Normalize(query);
            var candidates = CollectCandidates(queryTokens, normalizedQuery);
            if (candidates.Count == 0)
            {
                return Array.Empty<OperationSearchHit>();
            }

            var scored = new List<OperationSearchHit>(candidates.Count);
            foreach (var d in candidates)
            {
                var score = ScoreDocument(d, queryTokens, normalizedQuery);
                if (score > 0.0)
                {
                    scored.Add(new OperationSearchHit(_operations[d], score));
                }
            }

            scored.Sort(CompareHits);
            if (scored.Count > limit)
            {
                scored.RemoveRange(limit, scored.Count - limit);
            }
            return scored;
        }

        private static int CompareHits(OperationSearchHit a, OperationSearchHit b)
        {
            var byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0)
            {
                return byScore;
            }
            var byId = string.CompareOrdinal(
                a.Operation.Descriptor.Id, b.Operation.Descriptor.Id);
            if (byId != 0)
            {
                return byId;
            }
            return a.Operation.Descriptor.Version.CompareTo(b.Operation.Descriptor.Version);
        }

        private SortedSet<int> CollectCandidates(
            IReadOnlyList<string> queryTokens, string normalizedQuery)
        {
            var candidates = new SortedSet<int>();
            foreach (var token in queryTokens)
            {
                if (SearchTokenizer.IsWordToken(token))
                {
                    // Word tokens match index words by prefix: scan the word
                    // vocabulary once per query token.
                    foreach (var pair in _wordPostings)
                    {
                        if (pair.Key.StartsWith(token, StringComparison.Ordinal))
                        {
                            candidates.UnionWith(pair.Value);
                        }
                    }
                }
                else if (_bigramPostings.TryGetValue(token, out var docs))
                {
                    candidates.UnionWith(docs);
                }
            }
            for (var d = 0; d < _normalizedIds.Length; d++)
            {
                if (string.Equals(_normalizedIds[d], normalizedQuery, StringComparison.Ordinal))
                {
                    candidates.Add(d);
                }
            }
            return candidates;
        }

        private double ScoreDocument(
            int document, IReadOnlyList<string> queryTokens, string normalizedQuery)
        {
            if (string.Equals(
                    _normalizedIds[document], normalizedQuery, StringComparison.Ordinal))
            {
                return 1.0;
            }

            var sets = _fields[document];
            var words = _fieldWords[document];
            var best = 0.0;
            for (var f = 0; f < sets.Length; f++)
            {
                var fieldWords = words[f];
                // |Q∩F| counted on both sides of the match relation: a query
                // word token counts once no matter how many field words it
                // prefixes, a field word counts once no matter how many query
                // tokens prefix it, and a bigram token counts on both sides
                // at once (exact membership is injective — two distinct query
                // tokens can never equal the same field token). matched
                // therefore never exceeds |Q| or |F| and the Dice score stays
                // inside [0, 1].
                var queryHits = 0;
                var fieldHits = 0;
                foreach (var token in queryTokens)
                {
                    if (SearchTokenizer.IsWordToken(token))
                    {
                        for (var i = 0; i < fieldWords.Count; i++)
                        {
                            if (fieldWords[i].StartsWith(token, StringComparison.Ordinal))
                            {
                                queryHits++;
                                break;
                            }
                        }
                    }
                    else if (sets[f].Contains(token))
                    {
                        queryHits++;
                        fieldHits++;
                    }
                }
                for (var i = 0; i < fieldWords.Count; i++)
                {
                    var fieldWord = fieldWords[i];
                    foreach (var token in queryTokens)
                    {
                        if (SearchTokenizer.IsWordToken(token) &&
                            fieldWord.StartsWith(token, StringComparison.Ordinal))
                        {
                            fieldHits++;
                            break;
                        }
                    }
                }
                var matched = Math.Min(queryHits, fieldHits);
                if (matched != 0)
                {
                    var score = 2.0 * matched / (queryTokens.Count + sets[f].Count);
                    if (score > best)
                    {
                        best = score;
                    }
                }
            }
            return best;
        }

        private readonly struct FieldTokens
        {
            internal FieldTokens(HashSet<string>[] sets, List<string>[] words)
            {
                Sets = sets;
                Words = words;
            }

            internal HashSet<string>[] Sets { get; }

            internal List<string>[] Words { get; }
        }

        private static FieldTokens BuildFields(OperationDescriptor descriptor)
        {
            var localized = descriptor.Localized;
            var total = FirstLocalizedField + 3 * localized.Count;
            var sets = new HashSet<string>[total];
            var words = new List<string>[total];

            sets[FieldId] = TokenSet(SearchTokenizer.TokenizeIdentifier(descriptor.Id));
            sets[FieldDisplayName] = TokenSet(SearchTokenizer.Tokenize(descriptor.DisplayName));
            sets[FieldSummary] = TokenSet(SearchTokenizer.Tokenize(descriptor.Summary));
            sets[FieldTags] = TokenSetOfElements(descriptor.Tags);
            sets[FieldKeywords] = TokenSetOfElements(descriptor.Keywords);

            var field = FirstLocalizedField;
            foreach (var pair in localized)
            {
                var text = pair.Value;
                sets[field++] = TokenSet(SearchTokenizer.Tokenize(text.DisplayName));
                sets[field++] = TokenSet(SearchTokenizer.Tokenize(text.Summary));
                sets[field++] = TokenSetOfElements(text.Keywords);
            }

            for (var f = 0; f < sets.Length; f++)
            {
                var set = sets[f];
                var wordList = new List<string>();
                foreach (var token in set)
                {
                    if (SearchTokenizer.IsWordToken(token))
                    {
                        wordList.Add(token);
                    }
                }
                words[f] = wordList;
            }
            return new FieldTokens(sets, words);
        }

        private static HashSet<string> TokenSet(IReadOnlyList<string> tokens) =>
            new HashSet<string>(tokens, StringComparer.Ordinal);

        // A tag or keyword list indexes as one field: its elements join into a
        // single text before tokenization (SPEC §C5 item 1 names the list as
        // one indexed item).
        private static HashSet<string> TokenSetOfElements(IReadOnlyList<string> elements) =>
            TokenSet(SearchTokenizer.Tokenize(string.Join(" ", elements)));
    }
}
