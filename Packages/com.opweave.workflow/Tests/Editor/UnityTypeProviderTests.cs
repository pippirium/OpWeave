#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// [Edit] tests of the standard unity.* registration of T-W2.1-05 — the
    /// <see cref="UnityTypeProvider"/> definitions with their
    /// <see cref="IClrValueCodec{T}"/> conversions (SPEC §B4 test 1, §B7
    /// IClrValueCodec) and the <see cref="UnityClassRelationProvider"/> class
    /// relations behind SPEC §B2 test 2 and §B6 test 3.
    /// </summary>
    internal sealed class UnityTypeProviderTests
    {
        private static readonly TypeRegistryBuildResult s_build = TypeRegistryBuilder.Build(new[]
        {
            new TypeProviderCandidate(
                CoreTypeProvider.ProviderId, new CoreTypeProvider(), OperationOrigin.Standard),
            new TypeProviderCandidate(
                UnityTypeProvider.ProviderId, new UnityTypeProvider(), OperationOrigin.Standard),
        }, new UnityClassRelationProvider());

        // The test-local shape registry of SG-10 for §B8 fragment parity —
        // deliberately a different provider ID than the production provider.
        private sealed class ShapeDefinitions : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => UnityShapeTypes.All;
        }

        private static readonly ITypeRegistry s_shapeRegistry = TypeRegistryBuilder.Build(new[]
        {
            new TypeProviderCandidate(
                CoreTypeProvider.ProviderId, new CoreTypeProvider(), OperationOrigin.Standard),
            new TypeProviderCandidate(
                "test.unity-shapes", new ShapeDefinitions(), OperationOrigin.Standard),
        }).Registry;

        private static readonly UnityClassRelationProvider s_relations = new UnityClassRelationProvider();

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, "JsonValue.Parse failed for " + text);
            return parsed.Value!;
        }

        private static TypeRef T(string id)
        {
            var parsed = TypeRef.Parse(id);
            Assert.IsTrue(parsed.Ok, "TypeRef.Parse failed: " + id);
            return parsed.Value!;
        }

        private static TypeRef ClassLeaf(string className) =>
            new TypeRef("type:" + className, Array.Empty<TypeRef>());

        private static TypeDefinition Resolve(string typeId)
        {
            var resolved = s_build.Registry.Resolve(T(typeId));
            Assert.IsTrue(resolved.Ok, "Resolve failed: " + typeId);
            return resolved.Value!;
        }

        private static IClrValueCodec<T> ClrCodec<T>(string typeId)
        {
            var codec = Resolve(typeId).Codec;
            Assert.IsTrue(codec is IClrValueCodec<T>, typeId + " must carry a CLR codec");
            return (IClrValueCodec<T>)codec;
        }

        // SPEC §B4 test 1: JSON → C# → JSON. For inputs whose components are
        // exactly representable in single precision the round-trip is the
        // identical Canonical JSON; the persisted form is always the codec's
        // Normalize (SPEC §B7.1 item 2).
        private static void RoundTrip<T>(string typeId, string json, Action<T> checkClr)
        {
            var codec = ClrCodec<T>(typeId);
            var value = Json(json);
            Assert.AreEqual(0, codec.Validate(value, null).Count, typeId + " must validate");
            var clr = codec.FromJson(value);
            checkClr(clr);
            var back = codec.ToJson(clr);
            Assert.AreEqual(
                codec.Normalize(value).CanonicalText, back.CanonicalText,
                typeId + ": Normalize must be ToJson(FromJson(v)) (SPEC §B7.1 item 2)");
            Assert.AreEqual(
                back.CanonicalText, codec.Normalize(back).CanonicalText,
                typeId + ": Normalize must be idempotent (SPEC §B7.1)");
            Assert.AreEqual(
                back.CanonicalText,
                codec.ToJson(codec.FromJson(back)).CanonicalText,
                typeId + ": the persisted form must round-trip unchanged");
        }

        // ── Registration ────────────────────────────────────────────────────

        [Test]
        public void ProviderRegistersEveryUnityTypeWithoutFailures()
        {
            Assert.That(
                s_build.Failures.Count, Is.EqualTo(0),
                () => s_build.Failures.Count > 0 ? s_build.Failures[0].Detail : string.Empty);
            Assert.AreEqual(32, s_build.Registry.All.Count);
            foreach (var shape in UnityShapeTypes.All)
            {
                // An open-generic definition Id ("unity.ComponentRef<>") is
                // not a parseable TypeRef; close it with its declared
                // constraint — a class id always satisfies itself
                // (SPEC §B6 rule 1) — and resolve the closed use.
                var id = shape.GenericArity == 0
                    ? shape.Id
                    : shape.Id.Replace(
                        "<>", "<" + (shape.GenericConstraint ?? "core.Json") + ">");
                Assert.IsTrue(s_build.Registry.Resolve(T(id)).Ok, id);
            }
        }

        [Test]
        public void ProviderIsWiredNotDiscovered()
        {
            // Like CoreTypeProvider, the standard provider must not carry
            // [OpWeaveTypeProvider]: discovery would re-offer the same Type
            // IDs and §B0.1 would invalidate both candidates (SG-10).
            Assert.AreEqual(
                0,
                typeof(UnityTypeProvider)
                    .GetCustomAttributes(typeof(OpWeaveTypeProviderAttribute), false).Length);
            Assert.AreEqual("opweave.unity-types", UnityTypeProvider.ProviderId);
        }

        [Test]
        public void GetTypesIsDeterministicAndImmutable()
        {
            var provider = new UnityTypeProvider();
            var first = provider.GetTypes().ToList();
            var second = provider.GetTypes().ToList();
            Assert.AreEqual(UnityShapeTypes.All.Count, first.Count);
            Assert.IsTrue(first.SequenceEqual(second));
            for (var i = 0; i < first.Count; i++)
            {
                Assert.AreEqual(UnityShapeTypes.All[i].Id, first[i].Id);
            }
        }

        [Test]
        public void OnlyTheSixValueTypesCarryClrCodecs()
        {
            foreach (var id in new[]
            {
                "unity.Vector2", "unity.Vector3", "unity.Vector4",
                "unity.Quaternion", "unity.Euler", "unity.Color",
            })
            {
                Assert.IsTrue(IsClrCodec(Resolve(id).Codec), id);
            }
            foreach (var id in new[]
            {
                "unity.AssetPath", "unity.TypeName", "unity.HierarchyPath",
                "unity.ObjectRef", "unity.GameObjectRef", "unity.ComponentRef<unity.Component>",
                "unity.AssetRef<unity.Object>", "unity.GameObject",
            })
            {
                Assert.IsFalse(IsClrCodec(Resolve(id).Codec), id);
            }
        }

        private static bool IsClrCodec(IValueCodec codec) =>
            codec.GetType().GetInterfaces().Any(
                i => i.IsGenericType &&
                    i.GetGenericTypeDefinition() == typeof(IClrValueCodec<>));

        // ── SPEC §B4 test 1: value-type round-trips ─────────────────────────

        [Test]
        public void Vector2RoundTrips()
        {
            RoundTrip<Vector2>("unity.Vector2", "{\"x\":1.0,\"y\":-2.5}", v =>
            {
                Assert.AreEqual(1f, v.x);
                Assert.AreEqual(-2.5f, v.y);
            });
            Assert.AreEqual(
                "{\"x\":1e0,\"y\":-25e-1}",
                ClrCodec<Vector2>("unity.Vector2")
                    .ToJson(new Vector2(1f, -2.5f)).CanonicalText);
        }

        [Test]
        public void Vector3RoundTrips()
        {
            RoundTrip<Vector3>("unity.Vector3", "{\"x\":0.0,\"y\":1.0,\"z\":0.0}", v =>
            {
                Assert.AreEqual(new Vector3(0f, 1f, 0f), v);
            });
            Assert.AreEqual(
                "{\"x\":0e0,\"y\":1e0,\"z\":0e0}",
                ClrCodec<Vector3>("unity.Vector3")
                    .Normalize(Json("{\"x\":0,\"y\":1,\"z\":0}")).CanonicalText);
        }

        [Test]
        public void Vector4RoundTrips()
        {
            RoundTrip<Vector4>("unity.Vector4", "{\"x\":0.0,\"y\":1.0,\"z\":-2.5,\"w\":3.0}", v =>
            {
                Assert.AreEqual(new Vector4(0f, 1f, -2.5f, 3f), v);
            });
        }

        [Test]
        public void QuaternionReadsNormalized()
        {
            // SPEC §B4 "正規化して読む": an unnormalized read is normalized,
            // so {"w":2} persists as the identity quaternion.
            var codec = ClrCodec<Quaternion>("unity.Quaternion");
            var unnormalized = Json("{\"x\":0,\"y\":0,\"z\":0,\"w\":2}");
            Assert.AreEqual(0, codec.Validate(unnormalized, null).Count);
            Assert.AreEqual(Quaternion.identity, codec.FromJson(unnormalized));
            Assert.AreEqual(
                "{\"w\":1e0,\"x\":0e0,\"y\":0e0,\"z\":0e0}",
                codec.Normalize(unnormalized).CanonicalText);

            // A genuinely rotated value keeps its rotation.
            var rotated = codec.FromJson(Json("{\"x\":0,\"y\":0.7071067811865476,\"z\":0,\"w\":0.7071067811865476}"));
            Assert.AreEqual(1f, Quaternion.Dot(rotated, rotated), 1e-5f);
            Assert.AreEqual(0.7071068f, rotated.y, 1e-6f);

            // A zero-magnitude read maps to the identity (Unity's Normalize
            // convention for degenerate input).
            Assert.AreEqual(Quaternion.identity, codec.FromJson(Json("{\"x\":0,\"y\":0,\"z\":0,\"w\":0}")));
        }

        [Test]
        public void EulerRoundTripsAsItsOwnType()
        {
            RoundTrip<Vector3>("unity.Euler", "{\"x\":0.0,\"y\":90.0,\"z\":0.0}", v =>
            {
                Assert.AreEqual(new Vector3(0f, 90f, 0f), v);
            });
            Assert.AreNotSame(
                Resolve("unity.Euler").Codec.GetType(),
                Resolve("unity.Vector3").Codec.GetType());
        }

        [Test]
        public void ColorRoundTripsBothSpellings()
        {
            RoundTrip<Color>("unity.Color", "{\"r\":1.0,\"g\":0.0,\"b\":0.0,\"a\":1.0}", c =>
            {
                Assert.AreEqual(new Color(1f, 0f, 0f, 1f), c);
            });
            var codec = ClrCodec<Color>("unity.Color");
            var hex = Json("\"#FF0000FF\"");
            Assert.AreEqual(0, codec.Validate(hex, null).Count);
            Assert.AreEqual(new Color(1f, 0f, 0f, 1f), codec.FromJson(hex));
            Assert.AreEqual(
                "{\"a\":1e0,\"b\":0e0,\"g\":0e0,\"r\":1e0}",
                codec.Normalize(hex).CanonicalText,
                "both spellings of the same color share one persisted form (SPEC §B7.1)");
            var grey = codec.FromJson(Json("\"#80808080\""));
            Assert.AreEqual(128f / 255f, grey.r);
            Assert.AreEqual(128f / 255f, grey.a);
        }

        [Test]
        public void ClrCodecValidationDelegatesToTheShapeCodec()
        {
            // The SPEC §B7 test-1 shape through the registered codec: a
            // Vector3 missing 'z' is exactly one OW-TYPE-003 diagnostic.
            var diagnostics = Resolve("unity.Vector3").Codec.Validate(Json("{\"x\":1,\"y\":2}"), null);
            Assert.AreEqual(1, diagnostics.Count);
            Assert.AreEqual(ErrorCodes.OW_TYPE_003, diagnostics[0].Code);
            Assert.AreEqual(Severity.Error, diagnostics[0].Severity);
            StringAssert.Contains("z", diagnostics[0].Message);
        }

        // ── SPEC §B2 test 2: type: class id assignability ──────────────────

        [Test]
        public void TypeLeafAssignableToUnityComponent()
        {
            Assert.IsTrue(s_build.Registry.IsAssignable(
                ClassLeaf("UnityEngine.Animator"), T("unity.Component")).Assignable);
            Assert.IsTrue(s_build.Registry.IsAssignable(
                ClassLeaf("UnityEngine.Transform"), T("unity.Component")).Assignable);
            Assert.IsTrue(s_build.Registry.IsAssignable(
                ClassLeaf("UnityEngine.BoxCollider"), T("unity.Component")).Assignable);
            Assert.IsTrue(s_build.Registry.IsAssignable(
                ClassLeaf("UnityEngine.Animator"), T("unity.Object")).Assignable);

            // A project-side class id resolved through TypeCache.
            Assert.IsTrue(s_build.Registry.IsAssignable(
                ClassLeaf("OpWeave.UnityAdapter.Tests.ProbeComponent"),
                T("unity.Component")).Assignable);

            // The UnityEditor classes of the built-in table resolve too.
            Assert.IsTrue(s_build.Registry.IsAssignable(
                ClassLeaf("UnityEditor.MonoScript"), T("unity.MonoScript")).Assignable);
            Assert.IsTrue(s_build.Registry.IsAssignable(
                ClassLeaf("UnityEditor.Animations.AnimatorController"),
                T("unity.AnimatorController")).Assignable);

            // GameObject is not a Component — a determined 'false', not a guess.
            var wrong = s_build.Registry.IsAssignable(
                ClassLeaf("UnityEngine.GameObject"), T("unity.Component"));
            Assert.IsFalse(wrong.Assignable);
            StringAssert.Contains("not a subclass", wrong.Reason!);
        }

        // ── SPEC §B6 test 3: ComponentRef covariance ────────────────────────

        [Test]
        public void ComponentRefCovarianceThroughClassRelations()
        {
            Assert.IsTrue(s_build.Registry.IsAssignable(
                T("unity.ComponentRef<unity.Animator>"),
                T("unity.ComponentRef<unity.Component>")).Assignable);
            Assert.IsTrue(s_build.Registry.IsAssignable(
                T("unity.ComponentRef<type:UnityEngine.Animator>"),
                T("unity.ComponentRef<unity.Component>")).Assignable);
            Assert.IsTrue(s_build.Registry.IsAssignable(
                T("unity.ComponentRef<type:UnityEngine.Animator>"),
                T("unity.ComponentRef<type:UnityEngine.Component>")).Assignable);

            var reverse = s_build.Registry.IsAssignable(
                T("unity.ComponentRef<unity.Component>"),
                T("unity.ComponentRef<unity.Animator>"));
            Assert.IsFalse(reverse.Assignable);
            Assert.IsNotNull(reverse.Reason);
            var reverseLeaf = s_build.Registry.IsAssignable(
                T("unity.ComponentRef<type:UnityEngine.Component>"),
                T("unity.ComponentRef<unity.Animator>"));
            Assert.IsFalse(reverseLeaf.Assignable);
        }

        // ── Class-relation verdicts and the unresolved path ────────────────

        [Test]
        public void BuiltInClassIdsResolveToUnityClasses()
        {
            // The adapter's class table must cover every §B5 built-in class
            // ID registered in Core — a Core-only addition would report null.
            var classIds = new List<string>();
            foreach (var definition in UnityShapeTypes.All)
            {
                if (definition.Kind == TypeKind.Class)
                {
                    classIds.Add(definition.Id);
                }
            }
            Assert.AreEqual(10, classIds.Count);
            foreach (var id in classIds)
            {
                Assert.AreEqual(true, s_relations.IsSubclass(id, "unity.Object"), id);
                Assert.AreEqual(true, s_relations.IsSubclass(id, id), id);
            }
        }

        [Test]
        public void ClassRelationVerdicts()
        {
            Assert.AreEqual(true, s_relations.IsSubclass("unity.Animator", "unity.Component"));
            Assert.AreEqual(true, s_relations.IsSubclass("unity.Animator", "unity.Object"));
            Assert.AreEqual(false, s_relations.IsSubclass("unity.Component", "unity.Animator"));
            Assert.AreEqual(false, s_relations.IsSubclass("unity.GameObject", "unity.Component"));
            Assert.AreEqual(true, s_relations.IsSubclass("type:UnityEngine.Animator", "unity.Component"));
            Assert.AreEqual(true, s_relations.IsSubclass("type:UnityEngine.Animator", "type:UnityEngine.Component"));
            Assert.AreEqual(false, s_relations.IsSubclass("type:UnityEngine.Component", "type:UnityEngine.Animator"));
            Assert.AreEqual(true, s_relations.IsSubclass("type:UnityEditor.MonoScript", "unity.Object"));
        }

        [Test]
        public void UnresolvableClassIdsReportNullNotAssignable()
        {
            // Acceptance: unresolvable class IDs report null rather than
            // guessing — never true, never even a determined false.
            Assert.IsNull(s_relations.IsSubclass("type:No.Such.Class", "unity.Component"));
            Assert.IsNull(s_relations.IsSubclass("type:UnityEngine.Animator", "type:No.Such.Class"));
            Assert.IsNull(s_relations.IsSubclass("unity.NoSuchClass", "unity.Component"));
            Assert.IsNull(s_relations.IsSubclass("", "unity.Component"));

            // SPEC §B6 rule 4: at the registry level a null verdict is not
            // assignable and keeps the "unresolved class" reason.
            var result = s_build.Registry.IsAssignable(
                ClassLeaf("No.Such.Class"), T("unity.Component"));
            Assert.IsFalse(result.Assignable);
            StringAssert.Contains("Unresolved class", result.Reason!);
            var nested = s_build.Registry.IsAssignable(
                T("unity.ComponentRef<type:No.Such.Class>"),
                T("unity.ComponentRef<unity.Component>"));
            Assert.IsFalse(nested.Assignable);
            StringAssert.Contains("Unresolved class", nested.Reason!);
        }

        // ── §B8 schema parity with the Core shape codecs ────────────────────

        [Test]
        public void SchemaOutputMatchesTheCoreShapeFragments()
        {
            // The CLR codecs cannot implement Core's internal schema hook, so
            // the definitions declare TypeDefinition.Schema; the generated
            // fragment must be byte-identical to the shape codec's own
            // output — this is the drift guard between the two.
            foreach (var typeId in new[]
            {
                "unity.Vector2", "unity.Vector3", "unity.Vector4",
                "unity.Quaternion", "unity.Euler", "unity.Color",
            })
            {
                var production = TypeSchemas.Generate(s_build.Registry, T(typeId));
                var shape = TypeSchemas.Generate(s_shapeRegistry, T(typeId));
                Assert.IsTrue(production.Ok && shape.Ok, typeId);
                Assert.AreEqual(
                    shape.Value!.CanonicalText, production.Value!.CanonicalText, typeId);
                StringAssert.Contains("\"additionalProperties\":false",
                    production.Value!.CanonicalText);
            }
        }
    }

    /// <summary>
    /// A project-side Component subclass used to prove that
    /// <c>type:</c> class IDs resolve through TypeCache, not only the
    /// built-in short-name table. Never instantiated — metadata only.
    /// </summary>
    internal sealed class ProbeComponent : Component
    {
    }
}
