#nullable enable

using System;
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
    /// [Core] tests of
    /// <see cref="OperationSearchService.SearchCompatibleOperations"/> — the
    /// SPEC §C7 type-connection search (T-W3.1-02) exercised directly on the
    /// service over the catalog-mirroring fixtures of
    /// <see cref="SearchServiceFixtures"/>: both query forms, both
    /// directions, the exact/base/json match kinds and the
    /// <c>includeJson</c> gate.
    /// </summary>
    internal sealed class CompatibleOperationsSearchTests
    {
        private static RegistrySnapshot FullSnapshot() =>
            SearchServiceFixtures.Snapshot(includeCustom: true);

        private static IReadOnlyList<CompatibleOperationMatch> Search(
            CompatibleOperationsRequest request, RegistrySnapshot? snapshot = null)
        {
            var result = new OperationSearchService(snapshot ?? FullSnapshot())
                .SearchCompatibleOperations(request);
            Assert.IsTrue(result.Ok,
                "SearchCompatibleOperations failed: " + string.Join(
                    "; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
            return result.Value!;
        }

        private static List<string> Pairs(IReadOnlyList<CompatibleOperationMatch> matches) =>
            matches.Select(m => m.Operation.Descriptor.Id + ":" + m.Port).ToList();

        // ── SPEC §C7 the prefab.instantiate example ───────────────────────

        [Test]
        public void PrefabInstantiateInstanceDownstreamContainsApplyOverridesAndComponentAdd()
        {
            var matches = Search(CompatibleOperationsRequest.ForOperationPort(
                "unity.prefab.instantiate", "instance", CompatibleDirection.Downstream));

            var apply = matches.Single(m =>
                m.Operation.Descriptor.Id == "unity.prefab.apply_overrides");
            Assert.That(apply.Port, Is.EqualTo("instance"));
            Assert.That(apply.Match, Is.EqualTo(CompatibleMatchKind.Exact));
            Assert.That(apply.Operation.Origin, Is.EqualTo(OperationOrigin.Standard));

            var add = matches.Single(m =>
                m.Operation.Descriptor.Id == "unity.component.add");
            Assert.That(add.Port, Is.EqualTo("target"));
            Assert.That(add.Match, Is.EqualTo(CompatibleMatchKind.Base),
                "PrefabInstanceRef feeds the GameObjectRef input through its declared base");
        }

        [Test]
        public void TypeFormFindsTheSameConnectionsAsTheOperationForm()
        {
            var byType = Pairs(Search(CompatibleOperationsRequest.ForType(
                "unity.PrefabInstanceRef", CompatibleDirection.Downstream)));
            var byPort = Pairs(Search(CompatibleOperationsRequest.ForOperationPort(
                "unity.prefab.instantiate", "instance", CompatibleDirection.Downstream)));

            Assert.That(byPort, Is.EqualTo(byType));
        }

        // ── SPEC §C7 the AT-01 example ────────────────────────────────────

        [Test]
        public void GameObjectCreateDownstreamIncludesTheCustomOperation()
        {
            var matches = Search(CompatibleOperationsRequest.ForOperationPort(
                "unity.gameobject.create", "gameObject", CompatibleDirection.Downstream));

            var reset = matches.Single(m =>
                m.Operation.Descriptor.Id == "proj.transform.reset");
            Assert.That(reset.Port, Is.EqualTo("target"));
            Assert.That(reset.Match, Is.EqualTo(CompatibleMatchKind.Exact));
            Assert.That(reset.Operation.Origin, Is.EqualTo(OperationOrigin.Custom),
                "custom Operations are searched like standard ones (SPEC §M.7)");
        }

        // ── Direction and match kinds ─────────────────────────────────────

        [Test]
        public void UpstreamMatchesOutputsAssignableToTheType()
        {
            var matches = Search(CompatibleOperationsRequest.ForType(
                "unity.GameObjectRef", CompatibleDirection.Upstream));

            var pairs = Pairs(matches);
            // Exact GameObjectRef outputs lead, then the PrefabInstanceRef
            // outputs whose declared base is GameObjectRef.
            Assert.That(pairs[0], Is.EqualTo("proj.transform.reset:gameObject"));
            Assert.IsTrue(pairs.Contains("unity.gameobject.create:gameObject"));
            Assert.IsTrue(pairs.Contains("unity.gameobject.find:gameObject"));
            Assert.IsTrue(pairs.Contains("unity.gameobject.set_parent:gameObject"));
            Assert.IsTrue(pairs.Contains("unity.prefab.open_contents:root"));
            Assert.IsTrue(pairs.Contains("unity.prefab.unpack:gameObject"));

            var instantiate = matches.Single(m =>
                m.Operation.Descriptor.Id == "unity.prefab.instantiate");
            Assert.That(instantiate.Match, Is.EqualTo(CompatibleMatchKind.Base));
            // No output that merely carries JSON may claim a GameObjectRef.
            Assert.IsFalse(pairs.Any(p => p.StartsWith("unity.property.get:")));
        }

        [Test]
        public void ExactMatchesRankBeforeBaseAndJson()
        {
            var matches = Search(CompatibleOperationsRequest.ForType(
                "unity.GameObjectRef",
                CompatibleDirection.Downstream,
                includeJson: true));

            var kinds = matches.Select(m => m.Match).ToList();
            var firstBase = kinds.IndexOf(CompatibleMatchKind.Base);
            var firstJson = kinds.IndexOf(CompatibleMatchKind.Json);
            Assert.IsTrue(kinds.Take(firstBase).All(k => k == CompatibleMatchKind.Exact),
                "every exact match precedes every base match");
            Assert.IsTrue(kinds.Skip(firstBase).Take(firstJson - firstBase)
                .All(k => k == CompatibleMatchKind.Base),
                "every base match precedes every json match");
        }

        // ── includeJson (SPEC §C7) ────────────────────────────────────────

        [Test]
        public void JsonOnlyMatchesNeedIncludeJson()
        {
            var without = Search(CompatibleOperationsRequest.ForType(
                "unity.GameObjectRef", CompatibleDirection.Downstream));
            Assert.IsFalse(Pairs(without).Contains("unity.property.set:value"),
                "the core.Json input must be hidden without includeJson");

            var with = Search(CompatibleOperationsRequest.ForType(
                "unity.GameObjectRef", CompatibleDirection.Downstream, includeJson: true));
            var json = with.Single(m => m.Operation.Descriptor.Id == "unity.property.set" &&
                m.Port == "value");
            Assert.That(json.Match, Is.EqualTo(CompatibleMatchKind.Json));
        }

        [Test]
        public void QueryingJsonUpstreamGatesEveryConcreteOutput()
        {
            // Asking what feeds a core.Json position: a core.Json output is an
            // exact match, every other output connects only through §B6 rule 2.
            var matches = Search(CompatibleOperationsRequest.ForType(
                "core.Json", CompatibleDirection.Upstream, includeJson: true));

            var compile = matches.Single(m =>
                m.Operation.Descriptor.Id == "unity.script.compile");
            Assert.That(compile.Match, Is.EqualTo(CompatibleMatchKind.Exact));
            var create = matches.Single(m =>
                m.Operation.Descriptor.Id == "unity.gameobject.create");
            Assert.That(create.Match, Is.EqualTo(CompatibleMatchKind.Json));

            var gated = Search(CompatibleOperationsRequest.ForType(
                "core.Json", CompatibleDirection.Upstream));
            Assert.That(Pairs(gated), Is.EqualTo(new[]
            {
                "unity.package.add:package",
                "unity.property.get:value",
                "unity.script.compile:result",
            }), "without includeJson only the exact core.Json outputs remain");
        }

        // ── Nested core.Json inside generic arguments (SPEC §C7) ──────────

        // A conforming ITypeRegistry that is not a TypeRegistry: it delegates
        // every member to the fixture registry, so the §C7 classification may
        // not lean on the concrete implementation. The nested-generic cases
        // run against both shapes — wrapped: false pins the production
        // registry, wrapped: true pins the interface-only path.
        private sealed class DelegatingTypeRegistry : ITypeRegistry
        {
            private readonly ITypeRegistry _inner;

            internal DelegatingTypeRegistry(ITypeRegistry inner) => _inner = inner;

            public IReadOnlyList<TypeDefinition> All => _inner.All;

            public Result<TypeDefinition> Resolve(TypeRef type) => _inner.Resolve(type);

            public AssignResult IsAssignable(TypeRef source, TypeRef target) =>
                _inner.IsAssignable(source, target);
        }

        // The standard catalog declares no core.Json-bearing generic ports,
        // so the nested-JSON cases run against a dedicated fixture snapshot:
        // core.List<T> ports whose element position reaches core.Json only
        // through §B6 rule 2 inside the covariant argument.
        private static RegistrySnapshot GenericSnapshot(bool wrapped)
        {
            var snapshot = SearchServiceFixtures.SnapshotOf(new[]
            {
                Op.Define("test.list.consume_json", 1)
                    .Input("values", "core.List<core.Json>", true)
                    .Build(),
                Op.Define("test.list.consume_nested_json", 1)
                    .Input("values", "core.List<core.List<core.Json>>", true)
                    .Build(),
                Op.Define("test.list.consume_string", 1)
                    .Input("values", "core.List<core.String>", true)
                    .Build(),
                Op.Define("test.list.produce_string", 1)
                    .Output("values", "core.List<core.String>")
                    .Build(),
                Op.Define("test.list.produce_path", 1)
                    .Output("values", "core.List<unity.AssetPath>")
                    .Build(),
                Op.Define("test.list.produce_nested_string", 1)
                    .Output("values", "core.List<core.List<core.String>>")
                    .Build(),
            });
            return wrapped
                ? snapshot with { Types = new DelegatingTypeRegistry(snapshot.Types) }
                : snapshot;
        }

        private static CompatibleOperationMatch Single(
            IReadOnlyList<CompatibleOperationMatch> matches, string operationId) =>
            matches.Single(m => m.Operation.Descriptor.Id == operationId);

        [TestCase(false)]
        [TestCase(true)]
        public void ListStringToListJsonDownstreamIsAJsonMatch(bool wrapped)
        {
            // core.List<core.String> → core.List<core.Json> connects only
            // because §B6 rule 2 fires inside the covariant element — a json
            // match (SPEC §C7), hidden without includeJson.
            var gated = Search(CompatibleOperationsRequest.ForType(
                "core.List<core.String>", CompatibleDirection.Downstream),
                GenericSnapshot(wrapped));
            Assert.IsFalse(Pairs(gated).Contains("test.list.consume_json:values"),
                "a match that exists only through a nested core.Json is gated like a top-level one");

            var withJson = Search(CompatibleOperationsRequest.ForType(
                "core.List<core.String>", CompatibleDirection.Downstream, includeJson: true),
                GenericSnapshot(wrapped));
            Assert.That(Single(withJson, "test.list.consume_json").Match,
                Is.EqualTo(CompatibleMatchKind.Json));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NestedListJsonDownstreamIsAJsonMatch(bool wrapped)
        {
            // The same rule one level deeper: List<List<String>> connects to
            // List<List<Json>> only through the nested core.Json.
            var gated = Search(CompatibleOperationsRequest.ForType(
                "core.List<core.List<core.String>>", CompatibleDirection.Downstream),
                GenericSnapshot(wrapped));
            Assert.IsFalse(Pairs(gated).Contains("test.list.consume_nested_json:values"));

            var withJson = Search(CompatibleOperationsRequest.ForType(
                "core.List<core.List<core.String>>", CompatibleDirection.Downstream, includeJson: true),
                GenericSnapshot(wrapped));
            Assert.That(Single(withJson, "test.list.consume_nested_json").Match,
                Is.EqualTo(CompatibleMatchKind.Json));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ListStringToListJsonUpstreamIsAJsonMatch(bool wrapped)
        {
            // Upstream mirrors downstream: a List<String> output feeds a
            // List<Json> position only through the nested core.Json rule.
            var gated = Search(CompatibleOperationsRequest.ForType(
                "core.List<core.Json>", CompatibleDirection.Upstream), GenericSnapshot(wrapped));
            Assert.IsFalse(Pairs(gated).Contains("test.list.produce_string:values"));

            var withJson = Search(CompatibleOperationsRequest.ForType(
                "core.List<core.Json>", CompatibleDirection.Upstream, includeJson: true),
                GenericSnapshot(wrapped));
            Assert.That(Single(withJson, "test.list.produce_string").Match,
                Is.EqualTo(CompatibleMatchKind.Json));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NestedListJsonUpstreamIsAJsonMatch(bool wrapped)
        {
            var gated = Search(CompatibleOperationsRequest.ForType(
                "core.List<core.List<core.Json>>", CompatibleDirection.Upstream),
                GenericSnapshot(wrapped));
            Assert.IsFalse(Pairs(gated).Contains("test.list.produce_nested_string:values"));

            var withJson = Search(CompatibleOperationsRequest.ForType(
                "core.List<core.List<core.Json>>", CompatibleDirection.Upstream, includeJson: true),
                GenericSnapshot(wrapped));
            Assert.That(Single(withJson, "test.list.produce_nested_string").Match,
                Is.EqualTo(CompatibleMatchKind.Json));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void GenericPairAssignableWithoutJsonStaysBase(bool wrapped)
        {
            // Control: unity.AssetPath declares core.String as its base, so
            // List<AssetPath> → List<String> needs no core.Json rule at any
            // level — a base match in both directions.
            var downstream = Search(CompatibleOperationsRequest.ForType(
                "core.List<unity.AssetPath>", CompatibleDirection.Downstream),
                GenericSnapshot(wrapped));
            Assert.That(Single(downstream, "test.list.consume_string").Match,
                Is.EqualTo(CompatibleMatchKind.Base));

            var upstream = Search(CompatibleOperationsRequest.ForType(
                "core.List<core.String>", CompatibleDirection.Upstream), GenericSnapshot(wrapped));
            Assert.That(Single(upstream, "test.list.produce_path").Match,
                Is.EqualTo(CompatibleMatchKind.Base));
        }

        // ── Json-bearing constraints on unrelated definitions (SPEC §C7) ──

        // The provider answer for the fixture class pair: the registered
        // class-kind definitions carry no declared bases here, so the
        // relation is decided entirely by the snapshot's
        // IClassRelationProvider — the surface the production snapshot fills
        // through TypeCache (T-W2.1-05).
        private sealed class TestClassRelations : IClassRelationProvider
        {
            public bool? IsSubclass(string classId, string baseClassId) =>
                string.Equals(classId, "test.Dog", StringComparison.Ordinal) &&
                string.Equals(baseClassId, "test.Animal", StringComparison.Ordinal)
                    ? true
                    : (bool?)null;
        }

        private sealed class ListTypeProvider : ITypeProvider
        {
            private readonly IReadOnlyList<TypeDefinition> _definitions;

            internal ListTypeProvider(IReadOnlyList<TypeDefinition> definitions) =>
                _definitions = definitions;

            public IEnumerable<TypeDefinition> GetTypes() => _definitions;
        }

        // A dedicated snapshot for the unrelated-constraint shape: the
        // standard types plus the test.Dog/test.Animal class ids and an
        // unrelated generic definition whose lone GenericConstraint is
        // core.Json — registering it must not reclassify pairs that never
        // traverse it.
        private static RegistrySnapshot ClassRelationSnapshot(bool wrapped)
        {
            var build = TypeRegistryBuilder.Build(new[]
            {
                new TypeProviderCandidate(
                    CoreTypeProvider.ProviderId, new CoreTypeProvider(), OperationOrigin.Standard),
                new TypeProviderCandidate(
                    "opweave.unity-shapes",
                    new ListTypeProvider(UnityShapeTypes.All),
                    OperationOrigin.Standard),
                new TypeProviderCandidate(
                    "test.animals",
                    new ListTypeProvider(new TypeDefinition[]
                    {
                        new TypeDefinition(
                            "test.Animal", TypeKind.Class, 0, Array.Empty<string>(), null,
                            "Test base class id.", UnityClassCodec.Instance),
                        new TypeDefinition(
                            "test.Dog", TypeKind.Class, 0, Array.Empty<string>(), null,
                            "Test class id related to test.Animal only through the provider.",
                            UnityClassCodec.Instance),
                        new TypeDefinition(
                            "test.JsonHolder<>", TypeKind.Ref, 1, Array.Empty<string>(),
                            "core.Json",
                            "Unrelated generic whose argument must satisfy core.Json.",
                            StringCodec.Instance),
                    }),
                    OperationOrigin.Custom),
            }, new TestClassRelations());
            Assert.AreEqual(0, build.Failures.Count);

            var snapshot = SearchServiceFixtures.SnapshotOf(new[]
            {
                Op.Define("test.consume_animal", 1)
                    .Input("target", "test.Animal", true)
                    .Build(),
            }) with { Types = build.Registry };
            return wrapped
                ? snapshot with { Types = new DelegatingTypeRegistry(snapshot.Types) }
                : snapshot;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnrelatedJsonConstraintDoesNotHideAProviderSubclassPair(bool wrapped)
        {
            // An unrelated generic whose GenericConstraint carries core.Json
            // must not poison class pairs that never traverse it: the
            // provider-resolved test.Dog → test.Animal connection is a base
            // match under either includeJson and keeps the §C6 0.7 base-type
            // score, not the 0.1 json one.
            var snapshot = ClassRelationSnapshot(wrapped);

            var gated = Search(CompatibleOperationsRequest.ForType(
                "test.Dog", CompatibleDirection.Downstream), snapshot);
            Assert.That(Single(gated, "test.consume_animal").Match,
                Is.EqualTo(CompatibleMatchKind.Base),
                "the provider-resolved pair is a base match — never hidden behind includeJson");

            var withJson = Search(CompatibleOperationsRequest.ForType(
                "test.Dog", CompatibleDirection.Downstream, includeJson: true), snapshot);
            Assert.That(Single(withJson, "test.consume_animal").Match,
                Is.EqualTo(CompatibleMatchKind.Base),
                "includeJson must not relabel a Json-free connection as json");

            var page = new OperationSearchService(snapshot).SearchOperations(
                new OperationSearchRequest { AcceptsType = "test.Dog" });
            Assert.IsTrue(page.Ok,
                "SearchOperations failed: " + string.Join(
                    "; ", page.Diagnostics.Select(d => d.Code + " " + d.Message)));
            var item = page.Value!.Items.Single(i =>
                i.Operation.Descriptor.Id == "test.consume_animal");
            Assert.That(item.Score, Is.EqualTo(2.0 * 0.7).Within(1e-9),
                "the type term must be the 0.7 base score, not the 0.1 json score");
        }

        // ── Errors (SPEC §J codes only) ───────────────────────────────────

        [Test]
        public void UnknownOperationFailsOp001()
        {
            var result = new OperationSearchService(FullSnapshot())
                .SearchCompatibleOperations(CompatibleOperationsRequest.ForOperationPort(
                    "unity.missing.operation", "out", CompatibleDirection.Downstream));

            Assert.IsFalse(result.Ok);
            Assert.That(result.Diagnostics[0].Code, Is.EqualTo("OW-OP-001"));
        }

        [Test]
        public void UnknownPortFailsOp005()
        {
            var result = new OperationSearchService(FullSnapshot())
                .SearchCompatibleOperations(CompatibleOperationsRequest.ForOperationPort(
                    "unity.prefab.instantiate", "missing", CompatibleDirection.Downstream));

            Assert.IsFalse(result.Ok);
            Assert.That(result.Diagnostics[0].Code, Is.EqualTo("OW-OP-005"));
        }

        [Test]
        public void PortOnTheWrongSideFailsOp005()
        {
            // 'instance' is an output; asking upstream looks at inputs only.
            var result = new OperationSearchService(FullSnapshot())
                .SearchCompatibleOperations(CompatibleOperationsRequest.ForOperationPort(
                    "unity.prefab.instantiate", "instance", CompatibleDirection.Upstream));

            Assert.IsFalse(result.Ok);
            Assert.That(result.Diagnostics[0].Code, Is.EqualTo("OW-OP-005"));
        }

        [Test]
        public void MalformedTypeFailsType001()
        {
            var result = new OperationSearchService(FullSnapshot())
                .SearchCompatibleOperations(CompatibleOperationsRequest.ForType(
                    "not a type", CompatibleDirection.Downstream));

            Assert.IsFalse(result.Ok);
            Assert.That(result.Diagnostics[0].Code, Is.EqualTo("OW-TYPE-001"));
        }

        [Test]
        public void UnregisteredTypeFailsType001()
        {
            var result = new OperationSearchService(FullSnapshot())
                .SearchCompatibleOperations(CompatibleOperationsRequest.ForType(
                    "acme.Missing", CompatibleDirection.Downstream));

            Assert.IsFalse(result.Ok);
            Assert.That(result.Diagnostics[0].Code, Is.EqualTo("OW-TYPE-001"));
        }

        [Test]
        public void RepeatedQueriesReturnIdenticalResults()
        {
            var service = new OperationSearchService(FullSnapshot());
            var request = CompatibleOperationsRequest.ForType(
                "unity.GameObjectRef", CompatibleDirection.Downstream, includeJson: true);

            var first = service.SearchCompatibleOperations(request).Value!;
            var second = service.SearchCompatibleOperations(request).Value!;

            Assert.That(Pairs(second), Is.EqualTo(Pairs(first)));
        }
    }
}
