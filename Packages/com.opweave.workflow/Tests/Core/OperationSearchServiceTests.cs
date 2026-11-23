#nullable enable

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using OpWeave.Core.Search;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests of <see cref="OperationSearchService"/> — the SPEC §C6
    /// ranking, filtering, language and paging contract (T-W3.1-02) exercised
    /// directly on the service over the catalog-mirroring fixtures of
    /// <see cref="SearchServiceFixtures"/>, with no Agent API in between.
    /// </summary>
    internal sealed class OperationSearchServiceTests
    {
        // The standard catalog only — SPEC's draft-5.3 example enumerates the
        // standard Operations; the AT-01 custom Operation ties at the same
        // score and leads on the §C6.3 ID-Ordinal tie-break, which
        // CustomOperationTiesSortByIdOrdinal pins explicitly instead.
        private static RegistrySnapshot StandardSnapshot() =>
            SearchServiceFixtures.Snapshot(includeCustom: false);

        private static RegistrySnapshot FullSnapshot() =>
            SearchServiceFixtures.Snapshot(includeCustom: true);

        private static List<string> Ids(OperationSearchPage page) =>
            page.Items.Select(i => i.Operation.Descriptor.Id).ToList();

        private static OperationSearchPage Search(
            RegistrySnapshot snapshot, OperationSearchRequest request, string? defaultLanguage = null)
        {
            var result = new OperationSearchService(snapshot).SearchOperations(request, defaultLanguage);
            Assert.IsTrue(result.Ok,
                "SearchOperations failed: " + string.Join(
                    "; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
            return result.Value!;
        }

        // ── SPEC §C6 the draft-5.3 example ────────────────────────────────

        [Test]
        public void Draft53QueryRanksComponentAddFirst()
        {
            var page = Search(StandardSnapshot(), new OperationSearchRequest
            {
                Query = "Animator",
                AcceptsType = "unity.GameObjectRef",
                AllowedEffects = new[] { "scene.modify" },
            });

            Assert.That(Ids(page), Is.EqualTo(new[]
            {
                "unity.component.add",
                "unity.gameobject.create",
                "unity.gameobject.set_parent",
                "unity.prefab.instantiate",
                "unity.transform.set",
                "unity.gameobject.delete",
            }));
            Assert.That(page.Items[0].Score,
                Is.EqualTo(2.0).Within(1e-9),
                "exact type match and no other terms scores 2.0");
            Assert.That(page.Items[5].Score,
                Is.EqualTo(1.5).Within(1e-9),
                "unity.gameobject.delete pays the 0.5 destructive penalty");
        }

        // The honest §C6.3 consequence over the live catalog: the AT-01
        // custom Operation accepts GameObjectRef exactly, ties at 2.0 and
        // sorts before every unity.* ID by the ID-Ordinal tie-break. SPEC's
        // draft-5.3 example is the standard catalog above; this pins the
        // deterministic tie-break the same input produces when a custom
        // Operation competes (§M.7: custom Operations rank by the same rules).
        [Test]
        public void CustomOperationTiesSortByIdOrdinal()
        {
            var page = Search(FullSnapshot(), new OperationSearchRequest
            {
                Query = "Animator",
                AcceptsType = "unity.GameObjectRef",
                AllowedEffects = new[] { "scene.modify" },
            });

            Assert.That(page.Items[0].Operation.Descriptor.Id,
                Is.EqualTo("proj.transform.reset"));
            Assert.That(page.Items[0].Score, Is.EqualTo(2.0).Within(1e-9));
            Assert.That(page.Items[1].Operation.Descriptor.Id,
                Is.EqualTo("unity.component.add"));
        }

        // ── Filters (SPEC §C6 item 1) ─────────────────────────────────────

        [Test]
        public void OriginCustomReturnsOnlyCustomOperations()
        {
            var page = Search(FullSnapshot(), new OperationSearchRequest
            {
                Origin = OperationOrigin.Custom,
                Limit = 50,
            });

            Assert.That(page.Items, Is.Not.Empty);
            Assert.IsTrue(page.Items.All(i => i.Operation.Origin == OperationOrigin.Custom));
            Assert.That(Ids(page), Is.EqualTo(new[] { "proj.transform.reset" }));
        }

        [Test]
        public void OriginStandardExcludesCustomOperations()
        {
            var page = Search(FullSnapshot(), new OperationSearchRequest
            {
                Origin = OperationOrigin.Standard,
                Limit = 50,
            });

            Assert.IsTrue(page.Items.All(i => i.Operation.Origin == OperationOrigin.Standard));
            Assert.IsFalse(Ids(page).Contains("proj.transform.reset"));
        }

        [Test]
        public void CategoryFilterKeepsOnlyThatCategory()
        {
            var page = Search(StandardSnapshot(), new OperationSearchRequest
            {
                Category = "Prefab",
                Limit = 50,
            });

            // Every result ties at 0.0 except the destructive
            // apply_overrides (−0.5), which sorts last.
            Assert.That(Ids(page), Is.EqualTo(new[]
            {
                "unity.prefab.create_from_gameobject",
                "unity.prefab.create_variant",
                "unity.prefab.instantiate",
                "unity.prefab.open_contents",
                "unity.prefab.revert_overrides",
                "unity.prefab.save_contents",
                "unity.prefab.unpack",
                "unity.prefab.apply_overrides",
            }));
        }

        [Test]
        public void AllowedEffectsRequiresSubsetNotOverlap()
        {
            var page = Search(StandardSnapshot(), new OperationSearchRequest
            {
                AllowedEffects = new[] { "scene.modify" },
                Limit = 50,
            });

            // unity.property.set declares scene.modify + asset.modify and is
            // filtered out; unity.prefab.create_from_gameobject declares
            // asset.create + scene.modify and is filtered out too.
            var ids = Ids(page);
            Assert.IsTrue(page.Items.All(i =>
                i.Operation.Descriptor.SideEffects.All(e => e == "scene.modify")));
            Assert.IsFalse(ids.Contains("unity.property.set"));
            Assert.IsFalse(ids.Contains("unity.prefab.create_from_gameobject"));
            Assert.IsTrue(ids.Contains("unity.component.add"));
        }

        [Test]
        public void TagsFilterKeepsOperationsCarryingARequestedTag()
        {
            var page = Search(StandardSnapshot(), new OperationSearchRequest
            {
                Tags = new[] { "prefab" },
                Limit = 50,
            });

            Assert.IsTrue(page.Items.All(i =>
                i.Operation.Descriptor.Tags.Contains("prefab")));
            Assert.IsTrue(Ids(page).Contains("unity.prefab.instantiate"));
            Assert.IsFalse(Ids(page).Contains("unity.component.add"));
            // The §C6 tag term is the requested-tag ratio — the single
            // requested tag carried adds 0.5, minus the destructive penalty
            // where it applies.
            foreach (var item in page.Items)
            {
                Assert.That(item.Score, Is.EqualTo(
                    item.Operation.Descriptor.Destructive ? 0.0 : 0.5).Within(1e-9));
            }
        }

        [Test]
        public void AcceptsTypeAndProducesTypeConjoin()
        {
            // Operations that both consume and produce a GameObjectRef —
            // create_from_gameobject and instantiate qualify through the
            // PrefabInstanceRef output's declared GameObjectRef base.
            var page = Search(StandardSnapshot(), new OperationSearchRequest
            {
                AcceptsType = "unity.GameObjectRef",
                ProducesType = "unity.GameObjectRef",
                Limit = 50,
            });

            Assert.That(Ids(page), Is.EqualTo(new[]
            {
                "unity.gameobject.create",
                "unity.gameobject.set_parent",
                "unity.prefab.create_from_gameobject",
                "unity.prefab.instantiate",
            }));
        }

        [Test]
        public void ProducesTypeMatchesAssignableOutputs()
        {
            // A GameObjectRef position accepts GameObjectRef outputs (exact)
            // and PrefabInstanceRef outputs (base).
            var page = Search(StandardSnapshot(), new OperationSearchRequest
            {
                ProducesType = "unity.GameObjectRef",
                Limit = 50,
            });

            var ids = Ids(page);
            Assert.IsTrue(ids.Contains("unity.gameobject.create"), "exact");
            Assert.IsTrue(ids.Contains("unity.prefab.instantiate"), "base via PrefabInstanceRef");
            Assert.IsFalse(ids.Contains("unity.component.add"), "ComponentRef is no GameObjectRef");
            Assert.IsFalse(ids.Contains("unity.property.get"), "a core.Json output is no GameObjectRef");
        }

        [Test]
        public void AcceptsTypeAcceptsValuesIntoJsonPortsAtJsonScore()
        {
            // core.Json input ports accept any value (SPEC §B6 rule 2) at the
            // 0.1 json type score — below every concrete match.
            var page = Search(StandardSnapshot(), new OperationSearchRequest
            {
                AcceptsType = "unity.GameObjectRef",
                Limit = 50,
            });

            var material = page.Items.Single(i =>
                i.Operation.Descriptor.Id == "unity.asset.create_material");
            Assert.That(material.Score, Is.EqualTo(0.2).Within(1e-9),
                "a Json-only acceptance scores 2.0 × 0.1");
            Assert.That(
                page.Items.First(i =>
                    i.Operation.Descriptor.Id == "unity.component.add").Score,
                Is.GreaterThan(material.Score));
        }

        // ── Score and determinism (SPEC §C6 items 2–3) ────────────────────

        [Test]
        public void TextQueryRanksByTokenMatchRatio()
        {
            var page = Search(FullSnapshot(), new OperationSearchRequest
            {
                Query = "transform reset",
                Limit = 10,
            });

            // "transform" + "reset" both appear in the AT-01 Operation's
            // fields (ratio 1.0 → 3.0); unity.transform.set matches only
            // "transform" (ratio 0.5 → 1.5).
            Assert.That(page.Items[0].Operation.Descriptor.Id,
                Is.EqualTo("proj.transform.reset"));
            Assert.That(page.Items[0].Score, Is.EqualTo(3.0).Within(1e-9));
        }

        [Test]
        public void ExactIdQueryScoresTextOne()
        {
            var page = Search(StandardSnapshot(), new OperationSearchRequest
            {
                Query = "unity.component.remove",
                Limit = 50,
            });

            var hit = page.Items.Single(i =>
                i.Operation.Descriptor.Id == "unity.component.remove");
            // 3.0×1.0 text − 0.5 destructive.
            Assert.That(hit.Score, Is.EqualTo(2.5).Within(1e-9));
            Assert.That(page.Items[0].Operation.Descriptor.Id,
                Is.EqualTo("unity.component.remove"));
        }

        [Test]
        public void EmptyQueryRanksByTypeScoreAndIdTiebreak()
        {
            var page = Search(StandardSnapshot(), new OperationSearchRequest
            {
                Query = "   ",
                AcceptsType = "unity.PrefabInstanceRef",
                Limit = 50,
            });

            // Exact PrefabInstanceRef inputs score 2.0 and order by ID — the
            // destructive apply_overrides pays 0.5 and lands between them and
            // the 0.7 base matches that a PrefabInstanceRef value feeds through
            // its GameObjectRef/ObjectRef bases; the Json inputs trail at 0.1.
            Assert.That(Ids(page), Is.EqualTo(new[]
            {
                "unity.prefab.revert_overrides",
                "unity.prefab.unpack",
                "unity.prefab.apply_overrides",
                "unity.component.add",
                "unity.gameobject.create",
                "unity.gameobject.set_parent",
                "unity.prefab.create_from_gameobject",
                "unity.prefab.instantiate",
                "unity.prefab.save_contents",
                "unity.property.get",
                "unity.property.set",
                "unity.transform.set",
                "unity.gameobject.delete",
                "unity.asset.create_material",
                "unity.asset.create_scriptable_object",
            }));
            Assert.That(page.Items[0].Score, Is.EqualTo(2.0).Within(1e-9));
            Assert.That(page.Items[2].Score, Is.EqualTo(1.5).Within(1e-9), "exact minus destructive");
            Assert.That(page.Items[3].Score, Is.EqualTo(1.4).Within(1e-9), "2.0 × 0.7 base");
            Assert.That(page.Items[12].Score, Is.EqualTo(0.9).Within(1e-9), "base minus destructive");
            Assert.That(page.Items[13].Score, Is.EqualTo(0.2).Within(1e-9), "2.0 × 0.1 json");
        }

        [Test]
        public void RepeatedQueriesReturnIdenticalOrder()
        {
            var snapshot = FullSnapshot();
            var service = new OperationSearchService(snapshot);
            var request = new OperationSearchRequest { Query = "scene", Limit = 50 };

            var first = service.SearchOperations(request).Value!;
            var second = service.SearchOperations(request).Value!;

            Assert.That(Ids(second), Is.EqualTo(Ids(first)));
            Assert.That(
                second.Items.Select(i => i.Score),
                Is.EqualTo(first.Items.Select(i => i.Score)));
        }

        // ── Paging (SPEC §C6 item 4) ──────────────────────────────────────

        [Test]
        public void PagesReassembleTheRankedList()
        {
            var snapshot = StandardSnapshot();
            var service = new OperationSearchService(snapshot);
            var whole = service.SearchOperations(
                new OperationSearchRequest { Limit = 50 }).Value!;
            Assert.IsNull(whole.NextOffset);

            var walked = new List<string>();
            var offset = 0;
            while (true)
            {
                var page = service.SearchOperations(
                    new OperationSearchRequest { Limit = 10, Offset = offset }).Value!;
                walked.AddRange(Ids(page));
                if (page.NextOffset is not { } next)
                {
                    break;
                }
                offset = next;
            }
            Assert.That(walked, Is.EqualTo(Ids(whole)));
            Assert.That(Ids(whole).Count, Is.EqualTo(35), "the fixture catalog has 35 standard Operations");
        }

        [Test]
        public void LimitDefaultsToTenAndClampsToFifty()
        {
            var service = new OperationSearchService(StandardSnapshot());

            var defaulted = service.SearchOperations(new OperationSearchRequest()).Value!;
            Assert.That(defaulted.Items.Count, Is.EqualTo(10));
            Assert.That(defaulted.NextOffset, Is.EqualTo(10));

            // The §C6 50-item cap needs more than 50 candidates: pad the
            // catalog with dummy Operations so the clamp is observable.
            var descriptors = SearchServiceFixtures.Catalog(includeCustom: false);
            for (var i = 0; i < 30; i++)
            {
                descriptors.Add(Op.Define($"test.dummy.{i:D2}", 1).Summary("dummy").Build());
            }
            var clamped = new OperationSearchService(
                    SearchServiceFixtures.SnapshotOf(descriptors))
                .SearchOperations(new OperationSearchRequest { Limit = 500 }).Value!;
            Assert.That(clamped.Items.Count, Is.EqualTo(50));
            Assert.That(clamped.NextOffset, Is.EqualTo(50));
        }

        [Test]
        public void OffsetPastTheEndReturnsAnEmptyPage()
        {
            var page = Search(StandardSnapshot(),
                new OperationSearchRequest { Offset = 1000 });
            Assert.That(page.Items, Is.Empty);
            Assert.IsNull(page.NextOffset);
        }

        // ── Language (SPEC §C6 item 5) ────────────────────────────────────

        [Test]
        public void LangSelectsLocalizedTextWithEnglishFallback()
        {
            var snapshot = StandardSnapshot();

            var japanese = Search(snapshot, new OperationSearchRequest
            {
                Category = "Component", Lang = "ja", Limit = 50,
            });
            Assert.That(japanese.Items[0].DisplayName,
                Is.EqualTo("Component追加"),
                "ja picks the localized display name");

            var english = Search(snapshot, new OperationSearchRequest
            {
                Category = "Component", Lang = "en", Limit = 50,
            });
            Assert.That(english.Items[0].DisplayName, Is.EqualTo("Add Component"));

            var unknown = Search(snapshot, new OperationSearchRequest
            {
                Category = "Component", Lang = "fr", Limit = 50,
            });
            Assert.That(unknown.Items[0].DisplayName, Is.EqualTo("Add Component"),
                "a language with no text falls back to English");
        }

        [Test]
        public void DefaultLanguageAppliesWhenLangIsUnset()
        {
            var snapshot = StandardSnapshot();
            var request = new OperationSearchRequest { Category = "Component" };

            var viaDefault = Search(snapshot, request, defaultLanguage: "ja");
            Assert.That(viaDefault.Items[0].DisplayName, Is.EqualTo("Component追加"));

            var viaEnglish = Search(snapshot, request);
            Assert.That(viaEnglish.Items[0].DisplayName, Is.EqualTo("Add Component"));
        }

        // ── Errors ────────────────────────────────────────────────────────

        [Test]
        public void MalformedFilterTypeFailsWithType001()
        {
            var result = new OperationSearchService(StandardSnapshot())
                .SearchOperations(new OperationSearchRequest { AcceptsType = "not a type" });

            Assert.IsFalse(result.Ok);
            Assert.That(result.Diagnostics[0].Code, Is.EqualTo("OW-TYPE-001"));
        }

        [Test]
        public void UnregisteredFilterTypeFailsWithType001()
        {
            var result = new OperationSearchService(StandardSnapshot())
                .SearchOperations(new OperationSearchRequest { ProducesType = "acme.Missing" });

            Assert.IsFalse(result.Ok);
            Assert.That(result.Diagnostics[0].Code, Is.EqualTo("OW-TYPE-001"));
        }
    }
}
