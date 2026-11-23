#nullable enable

// T-W3.1-01 [Core] tests: local:T-W3.1-01:search-index — the SPEC §C5 index
// over catalog-mirroring descriptor fixtures: the 「プレハブ 作成」top-3,
// the transform-reset pair, the 1,000-Operation 50 ms bound, and the
// normalization/tokenization/rebuild rules C5 fixes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using OpWeave.Core.Search;

namespace OpWeave.Core.Tests
{
    internal sealed class OperationSearchIndexTests
    {
        private const string PrefabOp = "unity.prefab.create_from_gameobject";
        private const string ResetOp = "proj.transform.reset";

        private static OperationSearchIndex CatalogIndex() =>
            OperationSearchIndex.Build(SearchCatalogFixtures.Snapshot(
                SearchCatalogFixtures.Catalog()));

        private static List<string> Ids(IReadOnlyList<OperationSearchHit> hits) =>
            hits.Select(h => h.Operation.Descriptor.Id).ToList();

        // ── SPEC §C5 test: 「プレハブ 作成」→ top 3 ────────────────────────

        [Test]
        public void PrefabJapaneseQueryRanksCreateFromGameObjectInTop3()
        {
            var index = CatalogIndex();
            var hits = index.Search("プレハブ 作成");
            var rank = Ids(hits).IndexOf(PrefabOp);
            Assert.That(rank, Is.GreaterThanOrEqualTo(0).And.LessThan(3),
                "SPEC §C5: 「プレハブ 作成」 must rank " + PrefabOp +
                " in the top 3, got rank " + rank + " of " +
                string.Join(", ", Ids(hits).Take(6)));
        }

        // ── SPEC §C5 test: AT-01 first for both query languages ───────────

        [Test]
        public void TransformResetRanksAt01FirstInEnglishAndJapanese()
        {
            var index = CatalogIndex();
            foreach (var query in new[] { "transform reset", "トランスフォーム リセット" })
            {
                var hits = index.Search(query);
                Assert.That(hits.Count, Is.GreaterThan(0), "no hits for " + query);
                Assert.That(hits[0].Operation.Descriptor.Id, Is.EqualTo(ResetOp),
                    "SPEC §C5: " + query + " must rank the AT-01 Operation first, got " +
                    string.Join(", ", Ids(hits).Take(4)));
                Assert.That(hits[0].Operation.Origin, Is.EqualTo(OperationOrigin.Custom));
            }
        }

        // ── SPEC §C5 test: 1,000 dummy Operations within 50 ms ────────────

        [Test]
        public void ThousandDummyOperationsSearchWithin50Ms()
        {
            var descriptors = new List<OperationDescriptor>();
            for (var i = 0; i < 1000; i++)
            {
                descriptors.Add(
                    Op.Define("test.dummy.op" + i.ToString("D4"), 1)
                        .Summary("Dummy operation " + i + " mirroring catalog text volume.")
                        .Ja("ダミー操作" + i, "番号 " + i + " のダミー操作を実行します。", "ダミー", "操作")
                        .Build()
                        with
                        {
                            DisplayName = "Dummy Operation " + i,
                            Tags = new[] { "dummy", "bench" },
                            Keywords = new[] { "dummy", "operation", "bench" + (i % 10) },
                        });
            }
            var index = OperationSearchIndex.Build(
                SearchCatalogFixtures.Snapshot(descriptors));
            Assert.That(index.OperationCount, Is.EqualTo(1000));

            // A query every dummy matches, so all 1,000 documents are
            // candidates, scored and sorted — the honest §C5 bound, not a
            // zero-candidate fast path.
            index.Search("ダミー 操作", 50);
            var timer = Stopwatch.StartNew();
            var hits = index.Search("ダミー 操作", 50);
            timer.Stop();
            Assert.That(hits.Count, Is.EqualTo(50));
            TestContext.Out.WriteLine(
                "1,000-Operation search took {0} ms ({1} hits).",
                timer.Elapsed.TotalMilliseconds.ToString("F2"), hits.Count);
            Assert.That(timer.Elapsed.TotalMilliseconds, Is.LessThan(50.0),
                "SPEC §C5: a search over 1,000 dummy Operations must stay under 50 ms");
        }

        // ── §C5.2–3 normalization and tokenization ────────────────────────

        [Test]
        public void FullWidthAlnumAndLongVowelMarkAreInsensitive()
        {
            var index = CatalogIndex();
            // Full-width ASCII folds to half-width: 「ＴＲＡＮＳＦＯＲＭ」finds
            // the same word-token matches as "transform".
            var wide = index.Search("ＴＲＡＮＳＦＯＲＭ　ＲＥＳＥＴ");
            Assert.That(TopId(wide), Is.EqualTo(ResetOp));

            // The katakana long-vowel mark is never distinguished: 「プレハブー」
            // and 「プレハブ」produce identical results.
            var withMark = Ids(index.Search("プレハブー 作成"));
            var without = Ids(index.Search("プレハブ 作成"));
            Assert.That(withMark, Is.EqualTo(without));
            Assert.That(withMark.IndexOf(PrefabOp), Is.GreaterThanOrEqualTo(0).And.LessThan(3));
        }

        private static string TopId(IReadOnlyList<OperationSearchHit> hits) =>
            hits.Count == 0 ? string.Empty : hits[0].Operation.Descriptor.Id;

        [Test]
        public void IdSplitsOnDotsAndUnderscoresIntoWordTokens()
        {
            // "zeta" and "beta" live only inside the ID's '_' and '.'
            // segments; nothing in the text fields names them.
            var descriptor = Op.Define("acme.alpha_zeta.beta", 1)
                .Summary("Plain English words only.")
                .Build() with { DisplayName = "Neutral" };
            var index = OperationSearchIndex.Build(
                SearchCatalogFixtures.Snapshot(new[] { descriptor }));

            foreach (var query in new[] { "zeta", "beta", "alpha_zeta" })
            {
                var hits = index.Search(query);
                Assert.That(hits.Count, Is.EqualTo(1), "query " + query);
                Assert.That(hits[0].Operation.Descriptor.Id,
                    Is.EqualTo("acme.alpha_zeta.beta"));
            }
        }

        [Test]
        public void WordTokensMatchByPrefixAndQueriesUseQueryTokenization()
        {
            var index = CatalogIndex();
            // "transf" prefix-matches the "transform" word tokens of both the
            // standard transform.set and the AT-01 reset — but the reset
            // keeps first place because its fields cover the query better.
            var hits = index.Search("transf");
            var ids = Ids(hits);
            Assert.That(ids, Does.Contain(ResetOp));
            Assert.That(ids, Does.Contain("unity.transform.set"));

            // A query tokenizes like indexed text: 「プリミティブ」matches the
            // gameobject.create keyword bigrams.
            var primitive = index.Search("プリミティブ");
            Assert.That(Ids(primitive), Does.Contain("unity.gameobject.create"));
        }

        [Test]
        public void ExactIdMatchScoresOneAndRanksFirst()
        {
            var index = CatalogIndex();
            var hits = index.Search(PrefabOp);
            Assert.That(hits[0].Operation.Descriptor.Id, Is.EqualTo(PrefabOp));
            Assert.That(hits[0].Score, Is.EqualTo(1.0));
        }

        // ── rebuild and determinism ───────────────────────────────────────

        [Test]
        public void RebuiltIndexHoldsNoMemberOfThePreviousSnapshot()
        {
            // The index rides the published snapshot: each snapshot exposes
            // its own SearchIndex, so a newly published snapshot changes
            // search results without any manually rebuilt index (SPEC §C3
            // rule 8 — consumers use only the post-switch snapshot).
            var first = SearchCatalogFixtures.Snapshot(
                new[] { SearchCatalogFixtures.At01() });
            var second = SearchCatalogFixtures.Snapshot(
                SearchCatalogFixtures.StandardOperations());

            Assert.That(second.SearchIndex.OperationCount, Is.EqualTo(32));
            Assert.AreSame(second.SearchIndex, second.SearchIndex,
                "a snapshot builds its index once and keeps it");
            var hits = second.SearchIndex.Search("トランスフォーム リセット");
            Assert.That(Ids(hits), Does.Not.Contain(ResetOp),
                "a new snapshot's index must not retain an Operation it dropped");

            // The previous snapshot's own index still answers its own members.
            Assert.That(
                Ids(first.SearchIndex.Search("トランスフォーム リセット")),
                Does.Contain(ResetOp));
        }

        [Test]
        public void SameQueryAndIndexYieldIdenticalResults()
        {
            var index = CatalogIndex();
            var a = index.Search("シーン 作成", 10);
            var b = index.Search("シーン 作成", 10);
            Assert.That(Ids(a), Is.EqualTo(Ids(b)));
            Assert.That(a.Select(h => h.Score), Is.EqualTo(b.Select(h => h.Score)));
        }

        [Test]
        public void EmptyOrSeparatorOnlyQueriesReturnNoHits()
        {
            var index = CatalogIndex();
            Assert.That(index.Search(""), Is.Empty);
            Assert.That(index.Search("   "), Is.Empty);
            Assert.That(index.Search("...", 0), Is.Empty);
        }

        // ── whitespace is a token boundary, never an indexed character ────

        [Test]
        public void WhitespaceIsATokenBoundaryNotAToken()
        {
            // The F2 defect kept a one-character non-alphanumeric run
            // verbatim, so the separator in a multiword query was itself an
            // index term: " " matched every field that contained a space.
            Assert.That(SearchTokenizer.Tokenize("transform reset"),
                Is.EqualTo(new[] { "transform", "reset" }));
            Assert.That(SearchTokenizer.Tokenize(" "), Is.Empty);
            Assert.That(SearchTokenizer.Tokenize(" \t\r\n "), Is.Empty);
            // The ideographic space normalizes to a plain space and is a
            // boundary too.
            Assert.That(SearchTokenizer.Tokenize("プレハブ　作成"),
                Is.EqualTo(SearchTokenizer.Tokenize("プレハブ 作成")));
            Assert.That(SearchTokenizer.Tokenize("　"), Is.Empty);
        }

        [Test]
        public void WhitespaceOnlyAndSpacedQueriesReturnNoHits()
        {
            var index = CatalogIndex();
            // A single space used to be an indexed term: it must return no
            // hits now, like every other token-free query.
            Assert.That(index.Search(" "), Is.Empty);
            Assert.That(index.Search("\t"), Is.Empty);
            Assert.That(index.Search("　"), Is.Empty);
        }

        [Test]
        public void MultiwordQueryDoesNotMatchFieldsMerelyContainingSpaces()
        {
            // The query's spaces are boundaries, not terms: a document that
            // shares no token with the query stays out of the result set no
            // matter how many spaces its fields contain.
            var descriptor = Op.Define("acme.spacey", 1)
                .Summary("apple banana cherry")
                .Build() with { DisplayName = "Spaced Words", Keywords = new[] { "x y" } };
            var index = OperationSearchIndex.Build(
                SearchCatalogFixtures.Snapshot(new[] { descriptor }));
            Assert.That(index.Search("transform reset"), Is.Empty);
        }

        // ── the Dice score stays inside [0, 1] ───────────────────────────

        [Test]
        public void OverlappingQueryPrefixesCannotOutscoreAnExactIdMatch()
        {
            // The F4 defect counted each query token independently: "a.ab"
            // tokenizes to {a, ab} and both could prefix the single field
            // word "ab" of another descriptor, scoring 4/3 — over the exact
            // ID's 1.0. Counted on both sides the field is one match, so the
            // exact ID keeps first place.
            var exact = Op.Define("a.ab", 1)
                .Summary("unrelated text")
                .Build() with { DisplayName = "Exact Id" };
            var other = Op.Define("zz.other", 1)
                .Summary("ab")
                .Build() with { DisplayName = "Prefix Only" };
            var index = OperationSearchIndex.Build(
                SearchCatalogFixtures.Snapshot(new[] { exact, other }));

            var hits = index.Search("a.ab");
            Assert.That(hits[0].Operation.Descriptor.Id, Is.EqualTo("a.ab"));
            Assert.That(hits[0].Score, Is.EqualTo(1.0));
            foreach (var hit in hits)
            {
                Assert.That(hit.Score, Is.LessThanOrEqualTo(1.0),
                    "the Dice score must stay inside [0, 1]");
            }
        }
    }
}
