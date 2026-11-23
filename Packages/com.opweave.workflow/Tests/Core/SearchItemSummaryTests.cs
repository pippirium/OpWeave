#nullable enable

// T-W3.2-02 [Core] tests: local:T-W3.2-02:search-item-core — the SPEC
// §H3 per-item bound exercised directly on the OpWeave.Core builder the
// dotnet test project compiles: every built search_operations item is
// at most 600 UTF-8 bytes of Canonical JSON on its final form, reduced
// in exactly the §H3 order with the identity members intact. The same
// sources compile inside TestProject/ for the full Unity EditMode suite.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using OpWeave.Core.Search;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests of <see cref="SearchItemSummary"/> — the SPEC §H3
    /// "search の 1 件の上限" builder: the §C6 compact summary always
    /// fits 600 UTF-8 bytes of Canonical JSON, reduced in the SPEC order
    /// (marker → summary → displayName → outputs → inputs → effects),
    /// stopping at the first step that makes the item fit.
    /// </summary>
    internal sealed class SearchItemSummaryTests
    {
        private static readonly IReadOnlyDictionary<string, string> EmptyPorts =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private static int Bytes(JToken item) =>
            Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(item));

        private static string Wide(int length, char fill) => new string(fill, length);

        // An ordered port map in declaration order — the drop order the
        // §H3 steps 4–5 measure against.
        private static IReadOnlyDictionary<string, string> PortMap(
            params (string Name, string Type)[] ports)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var port in ports)
            {
                map.Add(port.Name, port.Type);
            }
            return map;
        }

        private static OperationSearchResult Result(
            string id,
            string displayName,
            string summary,
            IReadOnlyDictionary<string, string>? inputs = null,
            IReadOnlyDictionary<string, string>? outputs = null,
            IReadOnlyList<string>? effects = null,
            bool destructive = false,
            int version = 1,
            OperationOrigin origin = OperationOrigin.Custom)
        {
            var descriptor = Op.Define(id, version).Summary(summary).Build() with
            {
                DisplayName = displayName,
                SideEffects = effects ?? Array.Empty<string>(),
                Destructive = destructive,
            };
            return new OperationSearchResult(
                new RegisteredOperation(
                    descriptor, origin, "Example.Operations", null,
                    new string('a', 64), new string('b', 64)),
                displayName,
                summary,
                inputs ?? EmptyPorts,
                outputs ?? EmptyPorts,
                1.0);
        }

        private static void AssertNoLoneSurrogate(string text, string member)
        {
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]))
                {
                    Assert.IsTrue(
                        i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]),
                        member + " left a high surrogate unpaired at index " + i);
                    i++;
                }
                else
                {
                    Assert.IsFalse(
                        char.IsLowSurrogate(text[i]),
                        member + " left a low surrogate unpaired at index " + i);
                }
            }
        }

        // A typical compact item: every §C6 member present at full
        // length, inside the bound, and no 'truncated' marker.
        [Test]
        public void TypicalItem_FitsWithoutMarker()
        {
            var item = SearchItemSummary.Build(Result(
                "test.search.typical",
                "Add Component",
                "Adds a component to a GameObject.",
                inputs: PortMap(
                    ("target", "unity.GameObjectRef"),
                    ("componentType", "unity.TypeName")),
                outputs: PortMap(("component", "unity.ComponentRef<unity.Component>")),
                effects: new[] { "scene.modify" }));

            Assert.LessOrEqual(Bytes(item), SearchItemSummary.MaxBytes);
            Assert.IsNull(item["truncated"], "an unreduced item carries no marker");
            Assert.AreEqual("test.search.typical", (string?)item["id"]);
            Assert.AreEqual(1, (int?)item["version"]);
            Assert.AreEqual("custom", (string?)item["origin"]);
            Assert.AreEqual("Add Component", (string?)item["displayName"]);
            Assert.AreEqual("Adds a component to a GameObject.", (string?)item["summary"]);
            Assert.AreEqual(2, ((JObject)item["inputs"]!).Count);
            Assert.AreEqual(1, ((JObject)item["outputs"]!).Count);
            Assert.AreEqual(1, ((JArray)item["effects"]!).Count);
            Assert.IsFalse((bool?)item["destructive"]);
            Assert.AreEqual("immediate", (string?)item["mode"]);
        }

        // §H3 step 2 stops the reduction: a summary that alone pushes
        // the item over the bound yields partway while displayName and
        // every collection member stay untouched.
        [Test]
        public void Reduction_SummaryTrimSuffices()
        {
            var summary = Wide(700, 's');
            var item = SearchItemSummary.Build(Result(
                "test.search.summary",
                "Untouched Name",
                summary,
                inputs: PortMap(("text", "core.String")),
                outputs: PortMap(("done", "core.String")),
                effects: new[] { "scene.modify" }));

            Assert.LessOrEqual(Bytes(item), SearchItemSummary.MaxBytes);
            Assert.IsTrue((bool?)item["truncated"], "a reduced item declares the marker");
            var trimmed = (string?)item["summary"];
            Assert.IsNotNull(trimmed);
            Assert.Greater(trimmed!.Length, 0, "the trim stopped at the first fit");
            Assert.Less(trimmed.Length, summary.Length);
            StringAssert.StartsWith(trimmed, summary,
                "the summary is trimmed from the end — a prefix survives");
            Assert.AreEqual("Untouched Name", (string?)item["displayName"],
                "displayName is untouched while summary still yields bytes");
            Assert.AreEqual(1, ((JObject)item["outputs"]!).Count, "outputs untouched");
            Assert.AreEqual(1, ((JObject)item["inputs"]!).Count, "inputs untouched");
            Assert.AreEqual(1, ((JArray)item["effects"]!).Count, "effects untouched");
        }

        // §H3 step 3: the summary empties completely before displayName
        // yields, and the displayName trim again stops at the first fit.
        [Test]
        public void Reduction_DisplayNameTrimsAfterSummaryEmptied()
        {
            var displayName = Wide(500, 'd');
            var item = SearchItemSummary.Build(Result(
                "test.search.display",
                displayName,
                Wide(700, 's')));

            Assert.LessOrEqual(Bytes(item), SearchItemSummary.MaxBytes);
            Assert.IsTrue((bool?)item["truncated"]);
            Assert.AreEqual("", (string?)item["summary"],
                "summary reaches the empty string before displayName yields");
            var trimmed = (string?)item["displayName"];
            Assert.IsNotNull(trimmed);
            Assert.Greater(trimmed!.Length, 0, "the trim stopped at the first fit");
            Assert.Less(trimmed.Length, displayName.Length);
            StringAssert.StartsWith(trimmed, displayName);
            Assert.AreEqual(0, ((JObject)item["outputs"]!).Count, "port maps stay whole while text remains");
            Assert.AreEqual(0, ((JObject)item["inputs"]!).Count);
        }

        // §H3 step 4: with both texts exhausted the outputs entries drop
        // from the last declared port — the survivors keep declaration
        // order — while inputs and effects stay untouched.
        [Test]
        public void Reduction_OutputsDropFromTheLastDeclaredPort()
        {
            var declared = Enumerable.Range(0, 12)
                .Select(i => ("anOutputPortOfSomeWidth" + i, "core.String"))
                .ToArray();
            var item = SearchItemSummary.Build(Result(
                "test.search.outputs",
                Wide(500, 'd'),
                Wide(700, 's'),
                inputs: PortMap(("in", "core.String")),
                outputs: PortMap(declared),
                effects: new[] { "scene.modify" }));

            Assert.LessOrEqual(Bytes(item), SearchItemSummary.MaxBytes);
            Assert.AreEqual("", (string?)item["summary"]);
            Assert.AreEqual("", (string?)item["displayName"]);
            var kept = ((JObject)item["outputs"]!).Properties().Select(p => p.Name).ToList();
            Assert.Greater(kept.Count, 0, "the drop stopped at the first fit");
            Assert.Less(kept.Count, declared.Length);
            CollectionAssert.AreEqual(
                declared.Select(p => p.Item1).Take(kept.Count).ToList(), kept,
                "entries drop from the last declared port — a prefix survives");
            Assert.AreEqual(1, ((JObject)item["inputs"]!).Count,
                "inputs are untouched while outputs still yield entries");
            Assert.AreEqual(1, ((JArray)item["effects"]!).Count, "effects untouched");
        }

        // §H3 step 5: outputs exhaust completely before the inputs
        // entries drop — again from the last declared port — while
        // effects stay untouched.
        [Test]
        public void Reduction_InputsDropAfterOutputsExhausted()
        {
            var declared = Enumerable.Range(0, 12)
                .Select(i => ("anInputPortOfSomeWidth" + i, "core.String"))
                .ToArray();
            var item = SearchItemSummary.Build(Result(
                "test.search.inputs",
                Wide(500, 'd'),
                Wide(700, 's'),
                inputs: PortMap(declared),
                outputs: PortMap(("a", "core.String"), ("b", "core.String")),
                effects: new[] { "scene.modify" }));

            Assert.LessOrEqual(Bytes(item), SearchItemSummary.MaxBytes);
            Assert.AreEqual(0, ((JObject)item["outputs"]!).Count,
                "outputs exhaust completely before inputs yield");
            var kept = ((JObject)item["inputs"]!).Properties().Select(p => p.Name).ToList();
            Assert.Greater(kept.Count, 0, "the drop stopped at the first fit");
            Assert.Less(kept.Count, declared.Length);
            CollectionAssert.AreEqual(
                declared.Select(p => p.Item1).Take(kept.Count).ToList(), kept);
            Assert.AreEqual(1, ((JArray)item["effects"]!).Count, "effects untouched");
        }

        // §H3 step 6: every earlier field exhausted, the effects
        // elements drop off the end of the array.
        [Test]
        public void Reduction_EffectsDropFromTheEnd()
        {
            var effects = Enumerable.Range(0, 12)
                .Select(i => "effect.name." + i + "." + Wide(40, 'e'))
                .ToArray();
            var item = SearchItemSummary.Build(Result(
                "test.search.effects",
                Wide(500, 'd'),
                Wide(700, 's'),
                inputs: PortMap(("a", "core.String")),
                outputs: PortMap(("b", "core.String")),
                effects: effects));

            Assert.LessOrEqual(Bytes(item), SearchItemSummary.MaxBytes);
            Assert.AreEqual(0, ((JObject)item["outputs"]!).Count, "port maps exhaust before effects yield");
            Assert.AreEqual(0, ((JObject)item["inputs"]!).Count);
            var kept = ((JArray)item["effects"]!).Select(t => (string?)t).ToList();
            Assert.Greater(kept.Count, 0, "the drop stopped at the first fit");
            Assert.Less(kept.Count, effects.Length);
            CollectionAssert.AreEqual(
                effects.Take(kept.Count).ToList(), kept,
                "the surviving effects keep declaration order — drops come off the end");
            Assert.AreEqual("test.search.effects", (string?)item["id"]);
        }

        // A surrogate pair inside a trimmed text is never split: the
        // trim boundary crosses the pair and the whole pair drops, so
        // the emitted string never carries an unpaired surrogate (which
        // Canonical JSON rejects outright).
        [Test]
        public void Reduction_SurrogatePairsNeverSplit()
        {
            var summary = Wide(500, 's') + "🎮" + Wide(50, 'y');
            var item = SearchItemSummary.Build(Result(
                "test.search.surrogate",
                Wide(300, 'd') + "🎮",
                summary));

            Assert.LessOrEqual(Bytes(item), SearchItemSummary.MaxBytes);
            Assert.IsTrue((bool?)item["truncated"]);
            AssertNoLoneSurrogate((string?)item["summary"] ?? "", "summary");
            AssertNoLoneSurrogate((string?)item["displayName"] ?? "", "displayName");
            Assert.DoesNotThrow(
                () => CanonicalJson.Serialize(item),
                "Canonical JSON rejects unpaired surrogates — a split pair could never serialize");
        }

        // SPEC §H3 bound + §A4: a grammar-valid 128-character Operation
        // id leaves room for the identity members under the bound — the
        // reduced item keeps the full id and the unreduced
        // version/origin/destructive/mode while every other field
        // yields.
        [Test]
        public void MaxLengthId_KeepsFullIdAndIdentityMembers()
        {
            var id = "a." + new string('b', IdGrammar.OperationIdMaxLength - 2);
            Assert.AreEqual(IdGrammar.OperationIdMaxLength, id.Length);
            Assert.IsTrue(
                IdGrammar.IsValidOperationId(id),
                "the fixture id is a grammar-valid 128-character Operation id");
            var item = SearchItemSummary.Build(Result(
                id,
                Wide(500, 'd'),
                Wide(700, 's'),
                inputs: PortMap(Enumerable.Range(0, 10)
                    .Select(i => ("anInputPortOfSomeWidth" + i, "core.String")).ToArray()),
                outputs: PortMap(Enumerable.Range(0, 10)
                    .Select(i => ("anOutputPortOfSomeWidth" + i, "core.String")).ToArray()),
                effects: Enumerable.Range(0, 10)
                    .Select(i => "effect.name." + i + "." + Wide(40, 'e')).ToArray(),
                destructive: true,
                version: 7,
                origin: OperationOrigin.Standard));

            var bytes = Bytes(item);
            Assert.LessOrEqual(
                bytes, SearchItemSummary.MaxBytes,
                "the identity members always fit the bound (SPEC §H3): got " + bytes);
            Assert.AreEqual(id, (string?)item["id"],
                "the complete Operation id survives (SPEC §C6 item 6)");
            Assert.AreEqual(7, (int?)item["version"], "version is never reduced");
            Assert.AreEqual("standard", (string?)item["origin"], "origin is never reduced");
            Assert.IsTrue((bool?)item["destructive"], "destructive is never reduced");
            Assert.AreEqual("immediate", (string?)item["mode"], "mode is never reduced");
            Assert.IsTrue((bool?)item["truncated"]);
        }

        // The reduction is deterministic: the same result builds
        // byte-identical Canonical JSON on every call.
        [Test]
        public void SameResult_BuildsByteIdenticalItems()
        {
            OperationSearchResult Make() => Result(
                "test.search.deterministic",
                Wide(400, 'd'),
                Wide(700, 's'),
                inputs: PortMap(("a", "core.String"), ("b", "core.String")),
                outputs: PortMap(("c", "core.String"), ("d", "core.String")),
                effects: new[] { "scene.modify", "asset.create" });

            var first = CanonicalJson.Serialize(SearchItemSummary.Build(Make()));
            var second = CanonicalJson.Serialize(SearchItemSummary.Build(Make()));

            Assert.AreEqual(first, second, "the same input gives byte-identical items");
        }
    }
}
