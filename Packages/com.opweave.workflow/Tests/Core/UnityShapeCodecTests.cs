#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the Unity-independent JSON-shape codecs and
    /// <see cref="UnityShapeTypes"/> definitions of T-W1.4-05: SPEC §B3
    /// unity.* string types, §B4 value types, §B5 Ref types and class IDs, the
    /// §B7 Vector3 tests, and the §0.6 SG-10 registration contract — all run
    /// against an isolated, test-local <see cref="TypeRegistry"/> built from
    /// the same sources in both runtimes (dotnet and Unity EditMode).
    /// </summary>
    internal sealed class UnityShapeCodecTests
    {
        // SG-10: unity.* definitions register only through a provider wrapped
        // around the shared definition list — never a second production path.
        private sealed class UnityShapesProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => UnityShapeTypes.All;
        }

        private static readonly TypeRegistryBuildResult s_build = TypeRegistryBuilder.Build(new[]
        {
            new TypeProviderCandidate(
                CoreTypeProvider.ProviderId, new CoreTypeProvider(), OperationOrigin.Standard),
            new TypeProviderCandidate(
                "opweave.unity-shapes", new UnityShapesProvider(), OperationOrigin.Standard),
        });

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, "JsonValue.Parse failed for " + text);
            return parsed.Value!;
        }

        private static TypeRef Type(string id)
        {
            var parsed = TypeRef.Parse(id);
            Assert.IsTrue(parsed.Ok, "TypeRef.Parse failed: " + id);
            return parsed.Value!;
        }

        private static TypeDefinition Resolve(string typeId)
        {
            var resolved = s_build.Registry.Resolve(Type(typeId));
            Assert.IsTrue(resolved.Ok, "Resolve failed: " + typeId);
            return resolved.Value!;
        }

        private static IValueCodec Codec(string typeId) => Resolve(typeId).Codec;

        private static Diagnostic MustFailValidate(
            IValueCodec codec, JsonValue value, PortConstraints? constraints = null)
        {
            var diagnostics = codec.Validate(value, constraints);
            Assert.AreEqual(1, diagnostics.Count);
            var diagnostic = diagnostics[0];
            Assert.AreEqual(ErrorCodes.OW_TYPE_003, diagnostic.Code);
            Assert.AreEqual(Severity.Error, diagnostic.Severity);
            Assert.IsFalse(diagnostic.Retryable);
            return diagnostic;
        }

        // ── SG-10 / §B0.1: registration into a test-local registry ────────────

        [Test]
        public void TestRegistryRegistersEveryUnityTypeOnce()
        {
            Assert.AreEqual(0, s_build.Failures.Count);

            var ids = new List<string>();
            foreach (var definition in s_build.Registry.All)
            {
                ids.Add(definition.Id);
            }
            Assert.AreEqual(
                new[]
                {
                    "core.Bool", "core.Float", "core.Int", "core.Json", "core.List<>", "core.String",
                    "unity.Animator", "unity.AnimatorController", "unity.AssetPath", "unity.AssetRef<>",
                    "unity.Color", "unity.Component", "unity.ComponentRef<>", "unity.Euler",
                    "unity.GameObject", "unity.GameObjectRef", "unity.HierarchyPath", "unity.Material",
                    "unity.MonoScript", "unity.Object", "unity.ObjectRef", "unity.PrefabAssetRef",
                    "unity.PrefabInstanceRef", "unity.Quaternion", "unity.SceneRef",
                    "unity.ScriptableObject", "unity.Texture2D", "unity.Transform", "unity.TypeName",
                    "unity.Vector2", "unity.Vector3", "unity.Vector4",
                },
                ids.ToArray());
            Assert.AreEqual(32, ids.Count);
        }

        [Test]
        public void UnityDefinitionsCarryTheSpecShapes()
        {
            Assert.AreEqual(TypeKind.Primitive, Resolve("unity.AssetPath").Kind);
            Assert.AreEqual("core.String", Resolve("unity.AssetPath").BaseTypes[0]);
            Assert.AreEqual("core.String", Resolve("unity.TypeName").BaseTypes[0]);
            Assert.AreEqual("core.String", Resolve("unity.HierarchyPath").BaseTypes[0]);

            foreach (var id in new[]
            {
                "unity.Vector2", "unity.Vector3", "unity.Vector4",
                "unity.Quaternion", "unity.Euler", "unity.Color",
            })
            {
                Assert.AreEqual(TypeKind.Value, Resolve(id).Kind, id);
            }

            Assert.AreEqual(TypeKind.Class, Resolve("unity.GameObject").Kind);
            Assert.AreEqual("unity.Object", Resolve("unity.GameObject").BaseTypes[0]);
            Assert.AreEqual("unity.Component", Resolve("unity.Animator").BaseTypes[0]);

            Assert.AreEqual(TypeKind.Ref, Resolve("unity.ObjectRef").Kind);
            Assert.AreEqual("unity.ObjectRef", Resolve("unity.GameObjectRef").BaseTypes[0]);
            Assert.AreEqual("unity.GameObjectRef", Resolve("unity.PrefabInstanceRef").BaseTypes[0]);
            Assert.AreEqual(
                "unity.AssetRef<unity.GameObject>", Resolve("unity.PrefabAssetRef").BaseTypes[0]);
            // '<>' is the definition marker, never a resolvable use (SPEC §B1
            // rejects the empty argument list with OW-TYPE-005); resolving a
            // closed use returns the open definition unchanged because the
            // Ref codec does not bind its argument.
            var componentRef = Resolve("unity.ComponentRef<unity.Component>");
            Assert.AreEqual(1, componentRef.GenericArity);
            Assert.AreEqual("unity.Component", componentRef.GenericConstraint);
            var assetRef = Resolve("unity.AssetRef<unity.Object>");
            Assert.AreEqual(1, assetRef.GenericArity);
            Assert.AreEqual("unity.Object", assetRef.GenericConstraint);

            // Class-id generic arguments resolve (unity.Animator is registered
            // as a Class type; 'type:' leaves never resolve in Core).
            Assert.IsTrue(s_build.Registry.Resolve(Type("unity.ComponentRef<unity.Animator>")).Ok);
            Assert.IsTrue(s_build.Registry.Resolve(Type("unity.ComponentRef<type:MyGame.PC>")).Ok);
            Assert.IsTrue(s_build.Registry.Resolve(Type("unity.AssetRef<unity.Texture2D>")).Ok);
        }

        [Test]
        public void DuplicateUnityTypeRegistrationInvalidatesBothCandidates()
        {
            // SPEC §B0.1 / OW-OP-013: offering the same standard unity.* Type
            // ID twice — the situation SG-10's single-registration rule exists
            // to prevent — invalidates both candidates, never one.
            var result = TypeRegistryBuilder.Build(new[]
            {
                new TypeProviderCandidate(
                    "provider.one", new UnityShapesProvider(), OperationOrigin.Standard),
                new TypeProviderCandidate(
                    "provider.two", new UnityShapesProvider(), OperationOrigin.Standard),
            });

            var duplicates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var failure in result.Failures)
            {
                Assert.AreEqual(TypeRegistryFailureKind.DuplicateTypeId, failure.Kind);
                duplicates.Add(failure.TypeId!);
            }
            Assert.AreEqual(UnityShapeTypes.All.Count, duplicates.Count);
            Assert.IsFalse(
                result.Registry.Resolve(Type("unity.GameObjectRef")).Ok,
                "duplicated unity.GameObjectRef must not resolve");
            Assert.AreEqual(0, result.Registry.All.Count);
        }

        // ── SPEC §B3 unity.* string types ─────────────────────────────────────

        [Test]
        public void AssetPathAcceptsAssetsAndPackagesPaths()
        {
            var codec = Codec("unity.AssetPath");
            Assert.AreEqual(0, codec.Validate(Json("\"Assets/Scenes/Main.unity\""), null).Count);
            Assert.AreEqual(0, codec.Validate(Json("\"Assets/x\""), null).Count);
            Assert.AreEqual(0, codec.Validate(Json("\"Packages/com.opweave.workflow/x\""), null).Count);

            MustFailValidate(codec, Json("\"assets/lower/x\""));
            MustFailValidate(codec, Json("\"Library/x\""));
            MustFailValidate(codec, Json("\"Assetsx\""));
            MustFailValidate(codec, Json("\"\""));
            MustFailValidate(codec, Json("5"));
            MustFailValidate(codec, Json("null"));
        }

        [Test]
        public void AssetPathAppliesPortConstraints()
        {
            var codec = Codec("unity.AssetPath");
            var withEnum = new PortConstraints(new[] { "Assets/a" }, null, null, null, null);
            Assert.AreEqual(0, codec.Validate(Json("\"Assets/a\""), withEnum).Count);
            MustFailValidate(codec, Json("\"Assets/b\""), withEnum);
        }

        [Test]
        public void TypeNameAcceptsDottedNames()
        {
            var codec = Codec("unity.TypeName");
            Assert.AreEqual(0, codec.Validate(Json("\"UnityEngine.Animator\""), null).Count);
            Assert.AreEqual(0, codec.Validate(Json("\"A.B.C_1\""), null).Count);
            Assert.AreEqual(0, codec.Validate(Json("\"A\""), null).Count);

            MustFailValidate(codec, Json("\"\""));
            MustFailValidate(codec, Json("\"A.\""));
            MustFailValidate(codec, Json("\".A\""));
            MustFailValidate(codec, Json("\"A..B\""));
            MustFailValidate(codec, Json("\"A B\""));
            MustFailValidate(codec, Json("\"A-B\""));
            MustFailValidate(codec, Json("\"0A.B\""));
            MustFailValidate(codec, Json("5"));
        }

        [Test]
        public void HierarchyPathAcceptsRootedPaths()
        {
            var codec = Codec("unity.HierarchyPath");
            Assert.AreEqual(0, codec.Validate(Json("\"/Player\""), null).Count);
            Assert.AreEqual(0, codec.Validate(Json("\"/Root/Child/Grand\""), null).Count);
            Assert.AreEqual(0, codec.Validate(Json("\"/Root/Child[1]\""), null).Count);

            MustFailValidate(codec, Json("\"\""));
            MustFailValidate(codec, Json("\"/\""));
            MustFailValidate(codec, Json("\"Player\""));
            MustFailValidate(codec, Json("\"Root/Child\""));
            MustFailValidate(codec, Json("\"//A\""));
            MustFailValidate(codec, Json("\"/A//B\""));
            MustFailValidate(codec, Json("\"/A/\""));
            MustFailValidate(codec, Json("5"));
        }

        [Test]
        public void HierarchyPathHonoursTheEscapedSlash()
        {
            // SPEC §G2: a '/' inside a GameObject name is written '\/'. The
            // escaped pair is one name character, not a segment boundary, so
            // a name may contain or end with a real '/' (JSON "\\/" writes
            // the two characters '\' and '/').
            var codec = Codec("unity.HierarchyPath");
            Assert.AreEqual(0, codec.Validate(Json("\"/Foo\\\\/\""), null).Count);           // name 'Foo/'
            Assert.AreEqual(0, codec.Validate(Json("\"/A\\\\/B/C\""), null).Count);          // 'A/B', then 'C'
            Assert.AreEqual(0, codec.Validate(Json("\"/\\\\/\""), null).Count);              // the name '/'
            Assert.AreEqual(0, codec.Validate(Json("\"/Root/Child\\\\//Leaf\""), null).Count); // 'Child/', then 'Leaf'
            Assert.AreEqual(0, codec.Validate(Json("\"/A/\\\\//B\""), null).Count);          // 'A', the name '/', 'B'
            // A backslash not followed by '/' stays an ordinary name
            // character (SPEC defines no other escape): 'A\' and 'A\/B'.
            Assert.AreEqual(0, codec.Validate(Json("\"/A\\\\\""), null).Count);
            Assert.AreEqual(0, codec.Validate(Json("\"/A\\\\\\\\/B\""), null).Count);

            // The escape does not rescue an empty segment: an unescaped '/'
            // still separates, so a trailing or doubled separator fails.
            MustFailValidate(codec, Json("\"/A\\\\//\""));   // 'A/' then an empty segment
            MustFailValidate(codec, Json("\"/A//B\""));
        }

        [Test]
        public void HierarchyPathRejectsRepeatedEscapedSlashesInLinearTime()
        {
            // The segment alternatives are disjoint ('\/', a '\' not
            // followed by '/', or a character that is neither), so each
            // backslash is consumed exactly one way. A malformed path of
            // repeated escaped slashes ending in an unescaped '/' must be
            // rejected promptly — the earlier overlapping grammar let the
            // engine repartition the backslashes into exponentially many
            // parses and took seconds to answer.
            var codec = Codec("unity.HierarchyPath");
            var path = new StringBuilder("/a");
            for (var i = 0; i < 30; i++)
            {
                path.Append("\\/a");
            }
            path.Append('/');
            var value = JsonValue.From(new JValue(path.ToString()));

            codec.Validate(value, null);   // warm-up
            var watch = Stopwatch.StartNew();
            var diagnostics = codec.Validate(value, null);
            watch.Stop();

            Assert.AreEqual(1, diagnostics.Count);
            Assert.AreEqual(ErrorCodes.OW_TYPE_003, diagnostics[0].Code);
            Assert.Less(watch.ElapsedMilliseconds, 1000);
        }

        [Test]
        public void GameObjectRefPathHonoursTheEscapedSlash()
        {
            // SPEC §B5's 'path' key is a HierarchyPath: the same fixtures
            // must judge identically through the Ref codec.
            var codec = Codec("unity.GameObjectRef");
            Assert.AreEqual(
                0,
                codec.Validate(
                    Json("{\"kind\":\"gameObject\",\"path\":\"/Foo\\\\/\"}"), null).Count);
            Assert.AreEqual(
                0,
                codec.Validate(
                    Json("{\"kind\":\"gameObject\",\"path\":\"/A\\\\/B/C\"}"), null).Count);
            Assert.AreEqual(
                0,
                codec.Validate(
                    Json("{\"kind\":\"gameObject\",\"path\":\"/\\\\/\"}"), null).Count);
            Assert.AreEqual(
                0,
                codec.Validate(
                    Json("{\"kind\":\"gameObject\",\"path\":\"/Root/Child\\\\//Leaf\"}"), null).Count);
            Assert.AreEqual(
                0,
                codec.Validate(
                    Json("{\"kind\":\"gameObject\",\"path\":\"/Root/Child[1]\"}"), null).Count);

            MustFailValidate(codec, Json("{\"kind\":\"gameObject\",\"path\":\"\"}"));
            MustFailValidate(codec, Json("{\"kind\":\"gameObject\",\"path\":\"/\"}"));
            MustFailValidate(codec, Json("{\"kind\":\"gameObject\",\"path\":\"A/B\"}"));
            MustFailValidate(codec, Json("{\"kind\":\"gameObject\",\"path\":\"//A\"}"));
            MustFailValidate(codec, Json("{\"kind\":\"gameObject\",\"path\":\"/A/\"}"));
            MustFailValidate(codec, Json("{\"kind\":\"gameObject\",\"path\":\"/A\\\\//\"}"));
        }

        // ── SPEC §B4 value types ──────────────────────────────────────────────

        [Test]
        public void Vector3ValidatesShapeAndNormalizesToDoubles()
        {
            var codec = Codec("unity.Vector3");

            // SPEC §B7 test 2 / ticket acceptance: integer components are valid
            // and persist as Double tokens.
            Assert.AreEqual(0, codec.Validate(Json("{\"x\":0,\"y\":1,\"z\":0}"), null).Count);
            Assert.AreEqual(
                "{\"x\":0e0,\"y\":1e0,\"z\":0e0}",
                codec.Normalize(Json("{\"x\":0,\"y\":1,\"z\":0}")).CanonicalText);
            Assert.AreEqual(
                "{\"x\":5e-1,\"y\":-1e2,\"z\":0e0}",
                codec.Normalize(Json("{\"x\":0.5,\"y\":-1e2,\"z\":0}")).CanonicalText);
            Assert.AreEqual(
                "{\"x\":0e0,\"y\":1e0,\"z\":0e0}",
                codec.Normalize(Json("{\"x\":0.0,\"y\":1.0,\"z\":0.0}")).CanonicalText);

            // SPEC §B7 test 1: missing z is OW-TYPE-003.
            var missing = MustFailValidate(codec, Json("{\"x\":1,\"y\":2}"));
            StringAssert.Contains("z", missing.Message);

            MustFailValidate(codec, Json("{\"x\":0,\"y\":1,\"z\":2,\"w\":3}"));
            MustFailValidate(codec, Json("{\"x\":\"0\",\"y\":1,\"z\":0}"));
            MustFailValidate(codec, Json("{\"x\":0,\"y\":1,\"z\":null}"));
            MustFailValidate(codec, Json("5"));
            MustFailValidate(codec, Json("[0,1,2]"));
            MustFailValidate(codec, Json("\"v\""));
            MustFailValidate(codec, Json("true"));
        }

        [Test]
        public void VectorNormalizeIsIdempotentAndInputPreserving()
        {
            var codec = Codec("unity.Vector3");
            var value = Json("{\"x\":0,\"y\":1,\"z\":0}");
            var once = codec.Normalize(value);
            var twice = codec.Normalize(once);
            Assert.AreEqual("{\"x\":0,\"y\":1,\"z\":0}", value.CanonicalText);
            Assert.AreEqual(once.CanonicalText, twice.CanonicalText);
            Assert.AreEqual(once, twice);
        }

        [Test]
        public void OtherVectorTypesValidateTheirOwnShapes()
        {
            Assert.AreEqual(0, Codec("unity.Vector2").Validate(Json("{\"x\":0,\"y\":1}"), null).Count);
            MustFailValidate(Codec("unity.Vector2"), Json("{\"x\":0,\"y\":1,\"z\":2}"));
            MustFailValidate(Codec("unity.Vector2"), Json("{\"x\":0}"));

            Assert.AreEqual(
                0, Codec("unity.Vector4").Validate(Json("{\"x\":0,\"y\":1,\"z\":0,\"w\":1}"), null).Count);
            MustFailValidate(Codec("unity.Vector4"), Json("{\"x\":0,\"y\":1,\"z\":0}"));

            Assert.AreEqual(
                0, Codec("unity.Quaternion").Validate(Json("{\"x\":0,\"y\":0,\"z\":0,\"w\":1}"), null).Count);
            MustFailValidate(Codec("unity.Quaternion"), Json("{\"x\":0,\"y\":0,\"z\":0}"));

            Assert.AreEqual(
                0, Codec("unity.Euler").Validate(Json("{\"x\":0,\"y\":90,\"z\":0}"), null).Count);
            MustFailValidate(Codec("unity.Euler"), Json("{\"x\":0,\"y\":90}"));

            // unity.Euler is a distinct type from unity.Vector3 (SPEC §B4):
            // different Type IDs, different definitions, no base relation.
            var euler = Resolve("unity.Euler");
            var vector3 = Resolve("unity.Vector3");
            Assert.AreNotEqual(euler.Id, vector3.Id);
            Assert.AreNotSame(euler.Codec, vector3.Codec);
            Assert.AreEqual(0, euler.BaseTypes.Count);
            Assert.AreEqual(0, vector3.BaseTypes.Count);
        }

        [Test]
        public void VectorNormalizeIsDoubleOnlyAndLeavesNonObjects()
        {
            var codec = Codec("unity.Vector2");
            var scalar = Json("5");
            Assert.AreSame(scalar, codec.Normalize(scalar));
            var wrongShape = Json("{\"x\":0,\"w\":1}");
            var normalized = codec.Normalize(wrongShape);
            Assert.AreEqual("{\"w\":1,\"x\":0e0}", normalized.CanonicalText);
        }

        // ── SPEC §B4 unity.Color ──────────────────────────────────────────────

        [Test]
        public void ColorAcceptsObjectAndHexStringForms()
        {
            var codec = Codec("unity.Color");
            Assert.AreEqual(0, codec.Validate(Json("{\"r\":1,\"g\":1,\"b\":1,\"a\":1}"), null).Count);
            Assert.AreEqual(0, codec.Validate(Json("{\"r\":0.5,\"g\":0,\"b\":0,\"a\":1}"), null).Count);
            Assert.AreEqual(0, codec.Validate(Json("\"#FF0000FF\""), null).Count);
            Assert.AreEqual(0, codec.Validate(Json("\"#ff0000ff\""), null).Count);
            Assert.AreEqual(0, codec.Validate(Json("\"#00000080\""), null).Count);

            MustFailValidate(codec, Json("\"#F00\""));
            MustFailValidate(codec, Json("\"#FF0000\""));
            MustFailValidate(codec, Json("\"#FF0000FF0\""));
            MustFailValidate(codec, Json("\"FF0000FF\""));
            MustFailValidate(codec, Json("\"#GG0000FF\""));
            MustFailValidate(codec, Json("{\"r\":1,\"g\":1,\"b\":1}"));
            MustFailValidate(codec, Json("{\"r\":1,\"g\":1,\"b\":1,\"a\":1,\"x\":0}"));
            MustFailValidate(codec, Json("{\"r\":\"1\",\"g\":1,\"b\":1,\"a\":1}"));
            MustFailValidate(codec, Json("5"));
            MustFailValidate(codec, Json("[]"));
            MustFailValidate(codec, Json("true"));
        }

        [Test]
        public void ColorNormalizesBothSpellingsToOneObjectForm()
        {
            var codec = Codec("unity.Color");
            var fromHex = codec.Normalize(Json("\"#FF0000FF\""));
            var fromObject = codec.Normalize(Json("{\"r\":1,\"g\":0,\"b\":0,\"a\":1}"));
            Assert.AreEqual("{\"a\":1e0,\"b\":0e0,\"g\":0e0,\"r\":1e0}", fromHex.CanonicalText);
            Assert.AreEqual(fromHex, fromObject);
            Assert.AreEqual(fromHex.CanonicalText, fromObject.CanonicalText);
            Assert.AreEqual(fromHex.CanonicalText, codec.Normalize(fromHex).CanonicalText);

            var dim = codec.Normalize(Json("\"#00000080\""));
            var a = dim.ToToken()["a"]!.ToObject<double>();
            Assert.AreEqual(128.0 / 255.0, a, 1e-15);
        }

        // ── SPEC §B5 Ref types ────────────────────────────────────────────────

        [Test]
        public void GameObjectRefAcceptsTheSpecExample()
        {
            // SPEC §B5 test 1 / ticket acceptance: partial keys are allowed.
            Assert.AreEqual(
                0,
                Codec("unity.GameObjectRef").Validate(
                    Json("{\"kind\":\"gameObject\",\"path\":\"/Player\"}"), null).Count);
            Assert.AreEqual(
                0,
                Codec("unity.ObjectRef").Validate(
                    Json("{\"kind\":\"gameObject\",\"path\":\"/Player\"}"), null).Count);
        }

        [Test]
        public void RefWithoutKindFailsWithOwType003()
        {
            // SPEC §B5 test 2 / ticket acceptance.
            var diagnostic = MustFailValidate(Codec("unity.GameObjectRef"), Json("{\"path\":\"/Player\"}"));
            StringAssert.Contains("kind", diagnostic.Message);
            Assert.AreEqual("$.kind", diagnostic.JsonPath);

            MustFailValidate(Codec("unity.SceneRef"), Json("{\"path\":\"Assets/S.unity\"}"));
            MustFailValidate(Codec("unity.ComponentRef<unity.Component>"), Json("{\"index\":0}"));
        }

        [Test]
        public void RefKindDiscriminatorMustBeAKnownAcceptedString()
        {
            var codec = Codec("unity.GameObjectRef");
            MustFailValidate(codec, Json("{\"kind\":1}"));
            MustFailValidate(codec, Json("{\"kind\":null}"));
            MustFailValidate(codec, Json("{\"kind\":\"banana\"}"));
            MustFailValidate(codec, Json("{\"kind\":\"scene\",\"path\":\"Assets/S.unity\"}"));
            MustFailValidate(codec, Json("{\"kind\":\"asset\",\"guid\":\"g\"}"));
            MustFailValidate(codec, Json("{\"kind\":\"prefabAsset\",\"guid\":\"g\"}"));
        }

        [Test]
        public void RefKindFollowsTheDeclaredBaseChain()
        {
            // PrefabInstanceRef declares base GameObjectRef: its values are
            // valid where a GameObjectRef (or ObjectRef) is expected.
            var prefabInstance = Json("{\"kind\":\"prefabInstance\",\"path\":\"/P\",\"prefabAsset\":\"g\"}");
            Assert.AreEqual(0, Codec("unity.PrefabInstanceRef").Validate(prefabInstance, null).Count);
            Assert.AreEqual(0, Codec("unity.GameObjectRef").Validate(prefabInstance, null).Count);
            Assert.AreEqual(0, Codec("unity.ObjectRef").Validate(prefabInstance, null).Count);

            // A PrefabAssetRef value is never a GameObjectRef value — the
            // value-level counterpart of SPEC §B6's assignment rule.
            var prefabAsset = Json("{\"kind\":\"prefabAsset\",\"guid\":\"g\",\"path\":\"Assets/p.prefab\"}");
            Assert.AreEqual(0, Codec("unity.PrefabAssetRef").Validate(prefabAsset, null).Count);
            Assert.AreEqual(0, Codec("unity.AssetRef<unity.GameObject>").Validate(prefabAsset, null).Count);
            Assert.AreEqual(0, Codec("unity.ObjectRef").Validate(prefabAsset, null).Count);
            MustFailValidate(Codec("unity.GameObjectRef"), prefabAsset);
            MustFailValidate(Codec("unity.PrefabInstanceRef"), prefabAsset);

            var scene = Json("{\"kind\":\"scene\",\"path\":\"Assets/Main.unity\",\"guid\":\"g\"}");
            Assert.AreEqual(0, Codec("unity.SceneRef").Validate(scene, null).Count);
            Assert.AreEqual(0, Codec("unity.ObjectRef").Validate(scene, null).Count);
            MustFailValidate(Codec("unity.GameObjectRef"), scene);
        }

        [Test]
        public void RefRejectsUnknownKeysAndWrongMemberShapes()
        {
            var codec = Codec("unity.GameObjectRef");
            MustFailValidate(codec, Json("{\"kind\":\"gameObject\",\"path\":\"/P\",\"bogus\":1}"));
            MustFailValidate(codec, Json("{\"kind\":\"gameObject\",\"path\":\"Player\"}"));
            MustFailValidate(codec, Json("{\"kind\":\"gameObject\",\"path\":\"/A//B\"}"));
            MustFailValidate(codec, Json("{\"kind\":\"gameObject\",\"globalId\":5}"));
            MustFailValidate(codec, Json("{\"kind\":\"gameObject\",\"handle\":1.5}"));
            MustFailValidate(codec, Json("{\"kind\":\"gameObject\",\"handle\":\"abc\"}"));
            MustFailValidate(codec, Json("{\"kind\":\"gameObject\",\"prefabAsset\":\"g\"}"));
            MustFailValidate(codec, Json("{\"kind\":\"gameObject\",\"scene\":5}"));
            MustFailValidate(
                codec, Json("{\"kind\":\"gameObject\",\"scene\":{\"kind\":\"asset\"}}"));
            MustFailValidate(
                codec, Json("{\"kind\":\"gameObject\",\"scene\":{\"path\":\"Assets/S.unity\"}}"));
        }

        [Test]
        public void RefAcceptsSessionAndPersistentKeys()
        {
            var codec = Codec("unity.GameObjectRef");
            Assert.AreEqual(0, codec.Validate(Json("{\"kind\":\"gameObject\",\"handle\":-1338}"), null).Count);
            // 6000.5+ EntityId is a ulong that can exceed Int64: the digit-string
            // spelling is accepted (L-04).
            Assert.AreEqual(
                0,
                codec.Validate(
                    Json("{\"kind\":\"gameObject\",\"handle\":\"18446744073709550254\"}"), null).Count);
            Assert.AreEqual(0, codec.Validate(Json("{\"kind\":\"gameObject\",\"handle\":\"-1338\"}"), null).Count);
            Assert.AreEqual(
                0,
                codec.Validate(
                    Json("{\"kind\":\"gameObject\",\"globalId\":\"GlobalObjectId_V1-2-abc-1-0\"}"),
                    null).Count);
            Assert.AreEqual(
                0,
                codec.Validate(
                    Json("{\"kind\":\"gameObject\",\"scene\":{\"kind\":\"scene\",\"path\":\"Assets/S.unity\"}}"),
                    null).Count);
            Assert.AreEqual(
                0, codec.Validate(Json("{\"kind\":\"gameObject\",\"scene\":\"Assets/S.unity\"}"), null).Count);
        }

        [Test]
        public void SceneAndAssetRefsValidateTheirKeys()
        {
            var scene = Codec("unity.SceneRef");
            Assert.AreEqual(0, scene.Validate(Json("{\"kind\":\"scene\",\"path\":\"Assets/S.unity\"}"), null).Count);
            Assert.AreEqual(0, scene.Validate(Json("{\"kind\":\"scene\",\"guid\":\"g\"}"), null).Count);
            MustFailValidate(scene, Json("{\"kind\":\"scene\",\"path\":\"NotAnAssetPath\"}"));
            MustFailValidate(scene, Json("{\"kind\":\"scene\",\"handle\":3}"));

            var asset = Codec("unity.AssetRef<unity.Texture2D>");
            Assert.AreEqual(
                0, asset.Validate(Json("{\"kind\":\"asset\",\"guid\":\"g\",\"localId\":123}"), null).Count);
            Assert.AreEqual(
                0, asset.Validate(Json("{\"kind\":\"asset\",\"path\":\"Assets/m.mat\"}"), null).Count);
            MustFailValidate(asset, Json("{\"kind\":\"asset\",\"localId\":\"x\"}"));
            MustFailValidate(asset, Json("{\"kind\":\"asset\",\"localId\":1.5}"));
            MustFailValidate(asset, Json("{\"kind\":\"asset\",\"path\":\"/NotAPath\"}"));

            var prefabAsset = Codec("unity.PrefabAssetRef");
            Assert.AreEqual(
                0, prefabAsset.Validate(Json("{\"kind\":\"prefabAsset\",\"guid\":\"g\"}"), null).Count);
            MustFailValidate(prefabAsset, Json("{\"kind\":\"prefabAsset\",\"localId\":1}"));
        }

        [Test]
        public void ComponentRefValidatesOwnerTypeAndIndex()
        {
            var codec = Codec("unity.ComponentRef<unity.Animator>");
            Assert.AreEqual(
                0,
                codec.Validate(
                    Json("{\"kind\":\"component\",\"owner\":{\"kind\":\"gameObject\",\"path\":\"/P\"},\"type\":\"unity.Animator\",\"index\":0}"),
                    null).Count);
            Assert.AreEqual(
                0, codec.Validate(Json("{\"kind\":\"component\",\"type\":\"type:MyGame.PC\"}"), null).Count);
            Assert.AreEqual(
                0, codec.Validate(Json("{\"kind\":\"component\",\"type\":\"UnityEngine.Animator\"}"), null).Count);

            MustFailValidate(codec, Json("{\"kind\":\"component\",\"index\":-1}"));
            MustFailValidate(codec, Json("{\"kind\":\"component\",\"index\":1.5}"));
            MustFailValidate(codec, Json("{\"kind\":\"component\",\"type\":123}"));
            MustFailValidate(codec, Json("{\"kind\":\"component\",\"type\":\"not a class!!\"}"));
            MustFailValidate(codec, Json("{\"kind\":\"component\",\"owner\":\"/P\"}"));
            MustFailValidate(codec, Json("{\"kind\":\"component\",\"owner\":{\"kind\":\"scene\"}}"));
        }

        [Test]
        public void RefNormalizeConvertsDigitStringHandlesToIntegers()
        {
            var codec = Codec("unity.GameObjectRef");
            Assert.AreEqual(
                "{\"handle\":-1338,\"kind\":\"gameObject\"}",
                codec.Normalize(Json("{\"handle\":\"-1338\",\"kind\":\"gameObject\"}")).CanonicalText);
            // Beyond Int64 the digit string stays written — both spellings of
            // an in-range handle share one persisted form.
            Assert.AreEqual(
                "{\"handle\":\"18446744073709550254\",\"kind\":\"gameObject\"}",
                codec.Normalize(
                    Json("{\"handle\":\"18446744073709550254\",\"kind\":\"gameObject\"}")).CanonicalText);

            var integer = Json("{\"handle\":-1338,\"kind\":\"gameObject\"}");
            Assert.AreEqual(
                integer.CanonicalText,
                codec.Normalize(Json("{\"handle\":\"-1338\",\"kind\":\"gameObject\"}")).CanonicalText);
            Assert.AreSame(integer, codec.Normalize(integer));

            var component = Codec("unity.ComponentRef<unity.Component>");
            Assert.AreEqual(
                "{\"kind\":\"component\",\"owner\":{\"handle\":42,\"kind\":\"gameObject\"}}",
                component.Normalize(
                    Json("{\"kind\":\"component\",\"owner\":{\"handle\":\"42\",\"kind\":\"gameObject\"}}")).CanonicalText);

            var scalar = Json("5");
            Assert.AreSame(scalar, codec.Normalize(scalar));
        }

        // ── Trailing-newline regression (\z anchoring) ────────────────────────

        [Test]
        public void TrailingNewlineDoesNotSatisfyFullMatchPatterns()
        {
            // In .NET, '$' also matches immediately before a final '\n', so
            // every whole-string grammar is anchored with '\z' instead: a
            // newline suffix is real input and must fail with OW-TYPE-003.
            MustFailValidate(Codec("unity.Color"), Json("\"#FF0000FF\\n\""));
            MustFailValidate(Codec("unity.TypeName"), Json("\"UnityEngine.Animator\\n\""));
            MustFailValidate(
                Codec("unity.ComponentRef<unity.Component>"),
                Json("{\"kind\":\"component\",\"type\":\"UnityEngine.Animator\\n\"}"));
            MustFailValidate(
                Codec("unity.GameObjectRef"),
                Json("{\"kind\":\"gameObject\",\"handle\":\"42\\n\"}"));

            // HierarchyPath is unaffected: its '[^\\/]' segment grammar already
            // admits '\n' (GameObject names may contain anything but '/'), so
            // both anchors reach the same verdict there.
            Assert.AreEqual(
                0,
                Codec("unity.HierarchyPath").Validate(Json("\"/P\\n\""), null).Count);
        }

        [Test]
        public void InvalidColorStringIsNotNormalizedIntoAColor()
        {
            // A newline-suffixed hex string used to match ColorHex ('$'), and
            // Normalize then parsed the first eight digits into a valid color
            // while silently dropping the newline. It must stay written.
            var codec = Codec("unity.Color");
            var invalid = Json("\"#FF0000FF\\n\"");
            Assert.AreSame(invalid, codec.Normalize(invalid));

            var wrongLength = Json("\"#FF0000F\"");
            Assert.AreSame(wrongLength, codec.Normalize(wrongLength));
        }

        // ── Class-id types carry no JSON values (SPEC §B5) ────────────────────

        [Test]
        public void ClassIdTypesRejectEveryValue()
        {
            var codec = Codec("unity.GameObject");
            MustFailValidate(codec, Json("\"x\""));
            MustFailValidate(codec, Json("5"));
            MustFailValidate(codec, Json("null"));
            var value = Json("\"x\"");
            Assert.AreSame(value, codec.Normalize(value));
        }

        // ── ValueValidation integration (SPEC §A5/§B0.1 null rule) ────────────

        [Test]
        public void ValidationThroughTheRegistryAppliesTheNullRule()
        {
            Assert.AreEqual(
                0,
                ValueValidation.Validate(
                    s_build.Registry,
                    Type("unity.GameObjectRef"),
                    Json("{\"kind\":\"gameObject\",\"path\":\"/Player\"}"),
                    null,
                    false).Count);

            var denied = ValueValidation.Validate(
                s_build.Registry, Type("unity.GameObjectRef"), Json("null"), null, false);
            Assert.AreEqual(1, denied.Count);
            Assert.AreEqual(ErrorCodes.OW_TYPE_003, denied[0].Code);

            Assert.AreEqual(
                0,
                ValueValidation.Validate(
                    s_build.Registry, Type("unity.GameObjectRef"), Json("null"), null, true).Count);
        }

        [Test]
        public void NormalizeThroughTheRegistryUsesTheUnityCodec()
        {
            var normalized = ValueValidation.Normalize(
                s_build.Registry, Type("unity.Vector3"),
                Json("{\"x\":0,\"y\":1,\"z\":0}"), null, false);
            Assert.IsTrue(normalized.Ok);
            Assert.AreEqual("{\"x\":0e0,\"y\":1e0,\"z\":0e0}", normalized.Value!.CanonicalText);
        }
    }
}
