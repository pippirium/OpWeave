#nullable enable

using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpWeave.Core;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the SPEC §B6 assignability rules (T-W1.4-03):
    /// identity, <c>core.Json</c>, transitive BaseTypes, covariant generic
    /// arguments and class ids via <see cref="IClassRelationProvider"/>, the
    /// per-(S,T) cache, and the OW-TYPE-002 diagnostic with its candidate
    /// hint. The fixtures use the Unity-independent <c>unity.*</c>
    /// definitions of <see cref="UnityShapeTypes"/> (T-W1.4-05) registered
    /// through a test provider plus a stub class-relation provider, so the
    /// §B4 Euler/Vector3 case runs without Unity too.
    /// </summary>
    internal sealed class AssignabilityTests
    {
        private const string UnityShapesProviderId = "opweave.unity-shapes";

        private sealed class ListProvider : ITypeProvider
        {
            private readonly IReadOnlyList<TypeDefinition> _definitions;

            internal ListProvider(IReadOnlyList<TypeDefinition> definitions) =>
                _definitions = definitions;

            public IEnumerable<TypeDefinition> GetTypes() => _definitions;
        }

        private sealed class StubClassRelations : IClassRelationProvider
        {
            private readonly Dictionary<string, bool> _answers =
                new Dictionary<string, bool>(StringComparer.Ordinal);

            internal int Calls { get; private set; }

            internal void Answer(string classId, string baseClassId, bool verdict) =>
                _answers[classId + " → " + baseClassId] = verdict;

            public bool? IsSubclass(string classId, string baseClassId)
            {
                Calls++;
                return _answers.TryGetValue(classId + " → " + baseClassId, out var verdict)
                    ? verdict
                    : (bool?)null;
            }
        }

        private static TypeProviderCandidate Standard(string providerId, ITypeProvider provider) =>
            new TypeProviderCandidate(providerId, provider, OperationOrigin.Standard);

        private static ITypeRegistry Registry(IClassRelationProvider? relations = null)
        {
            var result = TypeRegistryBuilder.Build(new[]
            {
                Standard(CoreTypeProvider.ProviderId, new CoreTypeProvider()),
                Standard(UnityShapesProviderId, new ListProvider(UnityShapeTypes.All)),
            }, relations);
            Assert.AreEqual(0, result.Failures.Count);
            return result.Registry;
        }

        private static TypeRef T(string id)
        {
            var parsed = TypeRef.Parse(id);
            Assert.IsTrue(parsed.Ok, "test Type ID failed to parse: " + id);
            return parsed.Value!;
        }

        // A "type:" class-id leaf is not a top-level TypeId (SPEC §B1 allows it
        // only inside generic arguments), so it is constructed directly.
        private static TypeRef ClassLeaf(string className) =>
            new TypeRef("type:" + className, Array.Empty<TypeRef>());

        private static void MustBeAssignable(ITypeRegistry registry, string source, string target) =>
            MustBeAssignable(registry, T(source), T(target));

        private static void MustBeAssignable(ITypeRegistry registry, TypeRef source, TypeRef target)
        {
            var result = registry.IsAssignable(source, target);
            Assert.IsTrue(result.Assignable, $"{source} → {target}: {result.Reason}");
            Assert.IsNull(result.Reason);
        }

        private static AssignResult MustNotBeAssignable(ITypeRegistry registry, string source, string target) =>
            MustNotBeAssignable(registry, T(source), T(target));

        private static AssignResult MustNotBeAssignable(ITypeRegistry registry, TypeRef source, TypeRef target)
        {
            var result = registry.IsAssignable(source, target);
            Assert.IsFalse(result.Assignable, $"{source} → {target} unexpectedly assignable");
            Assert.IsNotNull(result.Reason);
            return result;
        }

        // ── Rule 1: identical types ────────────────────────────────────────────

        [Test]
        public void IdenticalTypesAreAssignable()
        {
            var registry = Registry();
            MustBeAssignable(registry, "core.Int", "core.Int");
            MustBeAssignable(registry, "unity.Vector3", "unity.Vector3");
            MustBeAssignable(registry, "unity.ComponentRef<unity.Animator>", "unity.ComponentRef<unity.Animator>");
            MustBeAssignable(registry, "unity.AssetRef<type:MyGame.Player>", "unity.AssetRef<type:MyGame.Player>");
        }

        // ── Rule 2: anything → core.Json ───────────────────────────────────────

        [Test]
        public void AnythingToCoreJsonIsAssignable()
        {
            var registry = Registry();
            MustBeAssignable(registry, "core.Int", "core.Json");
            MustBeAssignable(registry, "core.List<core.String>", "core.Json");
            MustBeAssignable(registry, "unity.Vector3", "core.Json");
            MustBeAssignable(registry, "unity.PrefabAssetRef", "core.Json");
            MustBeAssignable(registry, "unity.ComponentRef<unity.Animator>", "core.Json");
            MustBeAssignable(registry, ClassLeaf("MyGame.Player"), T("core.Json"));
        }

        // ── Rule 3: transitive BaseTypes ───────────────────────────────────────

        [Test]
        public void PrefabInstanceRefToGameObjectRefIsAssignable()
        {
            // SPEC §B6 [Core] test: a prefab instance is a scene GameObject.
            MustBeAssignable(Registry(), "unity.PrefabInstanceRef", "unity.GameObjectRef");
        }

        [Test]
        public void TransitiveBaseTypesAreAssignable()
        {
            var registry = Registry();
            MustBeAssignable(registry, "unity.PrefabInstanceRef", "unity.ObjectRef");
            MustBeAssignable(registry, "unity.PrefabAssetRef", "unity.AssetRef<unity.GameObject>");
            MustBeAssignable(registry, "unity.PrefabAssetRef", "unity.ObjectRef");
            MustBeAssignable(registry, "unity.AssetPath", "core.String");
            MustBeAssignable(registry, "unity.HierarchyPath", "core.String");
        }

        [Test]
        public void PrefabAssetRefToGameObjectRefIsNotAssignable()
        {
            // SPEC §B6 [Core] test: an asset is not a scene object.
            var result = MustNotBeAssignable(Registry(), "unity.PrefabAssetRef", "unity.GameObjectRef");
            StringAssert.Contains("unity.PrefabAssetRef", result.Reason!);
            StringAssert.Contains("unity.GameObjectRef", result.Reason!);
        }

        // ── Rule 5: no implicit conversion ─────────────────────────────────────

        [Test]
        public void EulerToVector3IsNotAssignable()
        {
            // SPEC §B4 [Core] test: unity.Euler is a distinct type from
            // unity.Vector3, not a base of it.
            var result = MustNotBeAssignable(Registry(), "unity.Euler", "unity.Vector3");
            StringAssert.Contains("unity.Euler", result.Reason!);
            StringAssert.Contains("unity.Vector3", result.Reason!);
        }

        [Test]
        public void IntToFloatIsNotAssignable()
        {
            // SPEC §B6 rule 5: no implicit conversion, including
            // core.Int → core.Float.
            var result = MustNotBeAssignable(Registry(), "core.Int", "core.Float");
            StringAssert.Contains("core.Int", result.Reason!);
            StringAssert.Contains("core.Float", result.Reason!);
        }

        [Test]
        public void UnrelatedStandardTypesAreNotAssignable()
        {
            var registry = Registry();
            MustNotBeAssignable(registry, "core.String", "core.Int");
            MustNotBeAssignable(registry, "core.Bool", "core.String");
            MustNotBeAssignable(registry, "unity.GameObjectRef", "unity.PrefabInstanceRef");
            MustNotBeAssignable(registry, "unity.SceneRef", "unity.GameObjectRef");
            MustNotBeAssignable(registry, "unity.Vector2", "unity.Euler");
        }

        // ── Rule 4: covariant generic arguments ───────────────────────────────

        [Test]
        public void ListArgumentsAreCovariant()
        {
            var registry = Registry();
            MustBeAssignable(registry, "core.List<unity.AssetPath>", "core.List<core.String>");
            MustBeAssignable(registry, "core.List<core.List<unity.AssetPath>>", "core.List<core.List<core.String>>");
            MustNotBeAssignable(registry, "core.List<core.String>", "core.List<unity.AssetPath>");
            MustNotBeAssignable(registry, "core.List<core.Int>", "core.List<core.Float>");
        }

        [Test]
        public void ComponentRefArgumentsAreCovariantThroughDeclaredBases()
        {
            // The built-in class ids encode their static Unity relations in
            // BaseTypes, so this covariance answers without a provider.
            var registry = Registry();
            MustBeAssignable(registry, "unity.ComponentRef<unity.Animator>", "unity.ComponentRef<unity.Component>");
            MustBeAssignable(registry, "unity.ComponentRef<unity.Transform>", "unity.ComponentRef<unity.Component>");
            MustBeAssignable(registry, "unity.AssetRef<unity.GameObject>", "unity.AssetRef<unity.Object>");
        }

        [Test]
        public void ComponentRefContravariantDirectionIsNotAssignable()
        {
            var relations = new StubClassRelations();
            relations.Answer("unity.Component", "unity.Animator", false);
            relations.Answer("unity.Component", "unity.Transform", false);
            var registry = Registry(relations);
            MustNotBeAssignable(registry, "unity.ComponentRef<unity.Component>", "unity.ComponentRef<unity.Animator>");
            MustNotBeAssignable(registry, "unity.AssetRef<unity.Object>", "unity.AssetRef<unity.GameObject>");
        }

        [Test]
        public void DifferentGenericDefinitionsAreNotCovariant()
        {
            var registry = Registry();
            MustNotBeAssignable(registry, "unity.ComponentRef<unity.Animator>", "unity.AssetRef<unity.Animator>");
            MustNotBeAssignable(registry, "core.List<core.String>", "unity.AssetRef<unity.Object>");
        }

        // ── Rule 4's class clause: IClassRelationProvider ─────────────────────

        [Test]
        public void TypeLeafToRegisteredClassUsesTheProvider()
        {
            // The §B2 [Edit] case shape, exercised in [Core] through a stub:
            // a type: class id is judged by IClassRelationProvider.
            var relations = new StubClassRelations();
            relations.Answer("type:MyGame.Player", "unity.Component", true);
            var registry = Registry(relations);
            MustBeAssignable(registry, ClassLeaf("MyGame.Player"), T("unity.Component"));
            MustBeAssignable(registry, "unity.ComponentRef<type:MyGame.Player>", "unity.ComponentRef<unity.Component>");
        }

        [Test]
        public void TypeLeafToTypeLeafUsesTheProvider()
        {
            var relations = new StubClassRelations();
            relations.Answer("type:MyGame.Player", "type:MyGame.Actor", true);
            relations.Answer("type:MyGame.Actor", "type:MyGame.Player", false);
            var registry = Registry(relations);
            MustBeAssignable(registry, ClassLeaf("MyGame.Player"), ClassLeaf("MyGame.Actor"));
            var result = MustNotBeAssignable(registry, ClassLeaf("MyGame.Actor"), ClassLeaf("MyGame.Player"));
            StringAssert.Contains("not a subclass", result.Reason!);
        }

        [Test]
        public void UnresolvedClassIsNotAssignableWithReason()
        {
            // The stub has no answer for this pair, and a registry built
            // without a provider cannot decide class relations either;
            // both paths end in "unresolved class" (SPEC §B6 rule 4).
            var withStub = Registry(new StubClassRelations());
            var result = MustNotBeAssignable(withStub, ClassLeaf("MyGame.Player"), T("unity.Component"));
            StringAssert.Contains("Unresolved class", result.Reason!);

            var withoutProvider = Registry();
            result = MustNotBeAssignable(
                withoutProvider, ClassLeaf("MyGame.Player"), ClassLeaf("MyGame.Actor"));
            StringAssert.Contains("Unresolved class", result.Reason!);
        }

        [Test]
        public void ClassIdAgainstNonClassIsNotAProviderQuestion()
        {
            var relations = new StubClassRelations();
            var registry = Registry(relations);
            MustNotBeAssignable(registry, ClassLeaf("MyGame.Player"), T("unity.Vector3"));
            MustNotBeAssignable(registry, "unity.Animator", "unity.GameObjectRef");
            Assert.AreEqual(0, relations.Calls, "non-class pairs must not consult the provider");
        }

        // ── Generic constraint well-formedness ─────────────────────────────────

        [Test]
        public void ConstraintViolatingArgumentIsNotAssignable()
        {
            // unity.ComponentRef<T> constrains T to unity.Component;
            // unity.GameObject is not a Component.
            var relations = new StubClassRelations();
            relations.Answer("unity.GameObject", "unity.Component", false);
            var registry = Registry(relations);
            var result = MustNotBeAssignable(registry, "unity.ComponentRef<unity.GameObject>", "unity.ObjectRef");
            StringAssert.Contains("generic constraint", result.Reason!);
        }

        [Test]
        public void UnresolvedConstrainedArgumentKeepsUnresolvedClassReason()
        {
            // SPEC §B6 rule 4: a null provider verdict inside a constrained
            // generic argument must surface as "unresolved class", not only as
            // a bare constraint violation, in both the AssignResult reason and
            // the OW-TYPE-002 hint.
            var registry = Registry(new StubClassRelations());
            var result = MustNotBeAssignable(
                registry, "unity.ComponentRef<type:MyGame.Player>", "unity.ComponentRef<unity.Component>");
            StringAssert.Contains("generic constraint", result.Reason!);
            StringAssert.Contains("Unresolved class", result.Reason!);

            var diagnostic = TypeAssignability.CreateNotAssignableDiagnostic(
                registry,
                T("unity.ComponentRef<type:MyGame.Player>"),
                T("unity.ComponentRef<unity.Component>"));
            Assert.IsNotNull(diagnostic);
            Assert.AreEqual(ErrorCodes.OW_TYPE_002, diagnostic!.Code);
            StringAssert.Contains("Unresolved class", diagnostic.Hint ?? string.Empty);
        }

        // ── Unregistered types ────────────────────────────────────────────────

        [Test]
        public void UnregisteredTypesAreNotAssignable()
        {
            var registry = Registry();
            var result = MustNotBeAssignable(registry, "acme.Missing", "core.String");
            StringAssert.Contains("not registered", result.Reason!);
            result = MustNotBeAssignable(registry, "core.String", "acme.Missing");
            StringAssert.Contains("not registered", result.Reason!);
            MustNotBeAssignable(registry, "acme.Missing", "unity.GameObjectRef");
        }

        // ── Per-(S,T) caching ─────────────────────────────────────────────────

        [Test]
        public void ResultsAreCachedPerPair()
        {
            var relations = new StubClassRelations();
            relations.Answer("type:MyGame.Player", "unity.Component", true);
            var registry = Registry(relations);
            var source = ClassLeaf("MyGame.Player");
            var target = T("unity.Component");

            var first = registry.IsAssignable(source, target);
            var calls = relations.Calls;
            Assert.IsTrue(calls > 0);
            var second = registry.IsAssignable(source, target);
            Assert.AreEqual(first, second, "cached and fresh results must be equal");
            Assert.AreEqual(calls, relations.Calls, "a cached pair must not re-consult the provider");
            MustBeAssignable(registry, "unity.PrefabInstanceRef", "unity.GameObjectRef");
            MustBeAssignable(registry, "unity.PrefabInstanceRef", "unity.GameObjectRef");

            // Negative answers are cached too: a repeated not-assignable pair
            // returns an equal result without re-consulting the provider.
            var negativeTarget = T("unity.GameObject");
            var firstMiss = registry.IsAssignable(source, negativeTarget);
            var missCalls = relations.Calls;
            var secondMiss = registry.IsAssignable(source, negativeTarget);
            Assert.AreEqual(firstMiss, secondMiss, "cached and fresh negative results must be equal");
            Assert.IsFalse(secondMiss.Assignable);
            Assert.AreEqual(missCalls, relations.Calls, "a cached negative pair must not re-consult the provider");
        }

        // ── OW-TYPE-002 diagnostics with candidate hints ──────────────────────

        [Test]
        public void NotAssignablePairProducesOwType002WithCandidates()
        {
            var registry = Registry();
            var diagnostic = TypeAssignability.CreateNotAssignableDiagnostic(
                registry, T("unity.PrefabAssetRef"), T("unity.GameObjectRef"),
                nodeId: "n1", port: "owner", jsonPath: "$.dataEdges[0]");

            Assert.IsNotNull(diagnostic);
            Assert.AreEqual(ErrorCodes.OW_TYPE_002, diagnostic!.Code);
            Assert.AreEqual(Severity.Error, diagnostic.Severity);
            Assert.IsFalse(diagnostic.Retryable);
            Assert.AreEqual("n1", diagnostic.NodeId);
            Assert.AreEqual("owner", diagnostic.Port);
            Assert.AreEqual("$.dataEdges[0]", diagnostic.JsonPath);
            StringAssert.Contains("unity.PrefabAssetRef", diagnostic.Message);
            StringAssert.Contains("unity.GameObjectRef", diagnostic.Message);

            var hint = diagnostic.Hint ?? string.Empty;
            StringAssert.Contains("not assignable", hint);
            // Sources that can feed unity.GameObjectRef.
            StringAssert.Contains("unity.PrefabInstanceRef", hint);
            // Targets unity.PrefabAssetRef can feed.
            StringAssert.Contains("unity.ObjectRef", hint);
            StringAssert.Contains("core.Json", hint);
        }

        [Test]
        public void AssignablePairProducesNoDiagnostic()
        {
            var registry = Registry();
            Assert.IsNull(TypeAssignability.CreateNotAssignableDiagnostic(
                registry, T("unity.PrefabInstanceRef"), T("unity.GameObjectRef")));
        }

        [Test]
        public void UnresolvedClassPairProducesOwType002WithReason()
        {
            var registry = Registry();
            var diagnostic = TypeAssignability.CreateNotAssignableDiagnostic(
                registry, ClassLeaf("MyGame.Player"), T("unity.Component"));
            Assert.IsNotNull(diagnostic);
            Assert.AreEqual(ErrorCodes.OW_TYPE_002, diagnostic!.Code);
            var hint = diagnostic.Hint ?? string.Empty;
            StringAssert.Contains("Unresolved class", hint);
            // The SPEC §B6 hint also lists the candidates that do connect:
            // the leaf still feeds core.Json, and Component's subclasses feed
            // the target.
            StringAssert.Contains("core.Json", hint);
            StringAssert.Contains("Sources assignable to 'unity.Component'", hint);
            StringAssert.Contains("unity.Transform", hint);
        }

        [Test]
        public void EulerToVector3ProducesOwType002()
        {
            var registry = Registry();
            var diagnostic = TypeAssignability.CreateNotAssignableDiagnostic(
                registry, T("unity.Euler"), T("unity.Vector3"));
            Assert.IsNotNull(diagnostic);
            Assert.AreEqual(ErrorCodes.OW_TYPE_002, diagnostic!.Code);
            var hint = diagnostic.Hint ?? string.Empty;
            // The hint carries the reason and both candidate directions.
            StringAssert.Contains("not assignable", hint);
            StringAssert.Contains("core.Json", hint);
            StringAssert.Contains("'unity.Vector3': unity.Vector3", hint);
        }

        [Test]
        public void IntToFloatProducesOwType002WithCandidates()
        {
            var registry = Registry();
            var diagnostic = TypeAssignability.CreateNotAssignableDiagnostic(
                registry, T("core.Int"), T("core.Float"));
            Assert.IsNotNull(diagnostic);
            Assert.AreEqual(ErrorCodes.OW_TYPE_002, diagnostic!.Code);
            var hint = diagnostic.Hint ?? string.Empty;
            StringAssert.Contains("not assignable", hint);
            // Candidates: core.Int can feed core.Json; only core.Float feeds
            // a core.Float input (rule 5, no implicit conversion).
            StringAssert.Contains("core.Json", hint);
            StringAssert.Contains("'core.Float': core.Float", hint);
        }

        // ── Candidate enumeration ─────────────────────────────────────────────

        [Test]
        public void SourcesAssignableToListsFeedingTypes()
        {
            var registry = Registry();
            var sources = TypeAssignability.SourcesAssignableTo(registry, T("unity.GameObjectRef"));
            var ids = new List<string>();
            foreach (var candidate in sources)
            {
                ids.Add(candidate.ToString());
            }
            Assert.AreEqual(
                new[] { "unity.GameObjectRef", "unity.PrefabInstanceRef" },
                ids.ToArray());
        }

        [Test]
        public void TargetsAssignableFromListsFeedableTypes()
        {
            var registry = Registry();
            var targets = TypeAssignability.TargetsAssignableFrom(registry, T("unity.PrefabAssetRef"));
            var ids = new List<string>();
            foreach (var candidate in targets)
            {
                ids.Add(candidate.ToString());
            }
            // Sorted Ordinal; open generic definitions are not complete types.
            Assert.AreEqual(
                new[] { "core.Json", "unity.ObjectRef", "unity.PrefabAssetRef" },
                ids.ToArray());
        }

        [Test]
        public void AssignableAndNonAssignableNullArgumentsAreRejected()
        {
            var registry = Registry();
            Assert.Throws<ArgumentNullException>(() => registry.IsAssignable(null!, T("core.String")));
            Assert.Throws<ArgumentNullException>(() => registry.IsAssignable(T("core.String"), null!));
            Assert.Throws<ArgumentNullException>(() =>
                TypeAssignability.CreateNotAssignableDiagnostic(null!, T("core.String"), T("core.Int")));
        }
    }
}
