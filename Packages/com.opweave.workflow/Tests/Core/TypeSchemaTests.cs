#nullable enable

using System.Collections.Generic;
using System.Linq;
#if !UNITY_6000_5_OR_NEWER
using System.Text.Json.Nodes;
using Json.Schema;
#endif
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the SPEC §B8 type → JSON Schema fragment generator
    /// (T-W1.4-06): fragments for <c>core.*</c> and <c>unity.*</c> types use
    /// only the §B8 keyword subset, referenced types land in <c>$defs</c>
    /// pointed to by <c>#/$defs/&lt;TypeId&gt;</c>, port constraints map to
    /// enum/pattern/minimum/maximum/maxLength, positive and negative values
    /// judge as expected under the test-only JsonSchema.Net 7.4.0 validator
    /// (the dependency chosen in T-W1.3-04), and identical input yields
    /// byte-identical output — pinned literals prove it across the dotnet
    /// and Unity runtimes that share these sources.
    /// </summary>
    internal sealed class TypeSchemaTests
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

#if !UNITY_6000_5_OR_NEWER
        private static readonly EvaluationOptions s_options =
            new EvaluationOptions { OutputFormat = OutputFormat.List };
#endif

        private static TypeRef Type(string id)
        {
            var parsed = TypeRef.Parse(id);
            Assert.IsTrue(parsed.Ok, "TypeRef.Parse failed: " + id);
            return parsed.Value!;
        }

        private static JsonValue Schema(
            string typeId,
            PortConstraints? constraints = null,
            string? description = null,
            bool nullable = false)
        {
            var result = TypeSchemas.Generate(s_build.Registry, Type(typeId), constraints, description, nullable);
            Assert.IsTrue(result.Ok, "TypeSchemas.Generate failed: " + typeId);
            return result.Value!;
        }

#if !UNITY_6000_5_OR_NEWER
        private static JsonSchema Compile(JsonValue schema) =>
            JsonSchema.FromText(schema.CanonicalText);

        private static void AssertValid(JsonValue schema, string instance)
        {
            var node = JsonNode.Parse(instance);
            var results = Compile(schema).Evaluate(node, s_options);
            Assert.IsTrue(results.IsValid, instance + " must validate");
        }

        private static void AssertInvalid(JsonValue schema, string instance)
        {
            var node = JsonNode.Parse(instance);
            var results = Compile(schema).Evaluate(node, s_options);
            Assert.IsFalse(results.IsValid, instance + " must fail validation");
        }
#endif

        private static void AssertKeywordsAreB8Subset(JToken token)
        {
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties())
                {
                    Assert.IsTrue(
                        TypeSchemas.AllowedKeywords.Contains(property.Name),
                        "keyword outside the SPEC §B8 subset: " + property.Name);
                    // The values of 'properties' and '$defs' map member/type
                    // names — not keywords — to schemas; audit the names' own
                    // keyword subset inside each mapped schema instead.
                    if (property.Name == "properties" || property.Name == "$defs")
                    {
                        foreach (var named in ((JObject)property.Value).Properties())
                        {
                            AssertKeywordsAreB8Subset(named.Value);
                        }
                    }
                    else
                    {
                        AssertKeywordsAreB8Subset(property.Value);
                    }
                }
            }
            else if (token is JArray array)
            {
                foreach (var item in array)
                {
                    AssertKeywordsAreB8Subset(item);
                }
            }
        }

        // The '$ref' members of a generated document, read at the schema
        // positions the keyword audit walks: 'properties' and '$defs' map
        // names to schemas, 'items'/'additionalProperties' each hold one and
        // an array at a schema position holds schemas element-wise. A string
        // inside 'enum' or 'required' is instance data, never a reference.
        private static List<string> CollectSchemaRefs(JToken token)
        {
            var references = new List<string>();
            if (token is JObject schema)
            {
                foreach (var property in schema.Properties())
                {
                    switch (property.Name)
                    {
                        case "$ref":
                            if (property.Value is JValue { Type: JTokenType.String } reference)
                            {
                                references.Add((string)reference.Value!);
                            }
                            break;
                        case "properties":
                        case "$defs":
                            if (property.Value is JObject named)
                            {
                                foreach (var member in named.Properties())
                                {
                                    references.AddRange(CollectSchemaRefs(member.Value));
                                }
                            }
                            break;
                        case "items":
                        case "additionalProperties":
                            references.AddRange(CollectSchemaRefs(property.Value));
                            break;
                    }
                }
            }
            else if (token is JArray array)
            {
                foreach (var item in array)
                {
                    references.AddRange(CollectSchemaRefs(item));
                }
            }
            return references;
        }

        // The one PortConstraints set the whole-set sweeps pass to every
        // fixture, so a sweep asserts the same shape in every test that
        // uses it (every keyword it maps is exercised on every fragment).
        private static readonly PortConstraints s_sweepConstraints =
            new PortConstraints(new[] { "a" }, "^p", 1.0, 9.0, 8);

        // Every resolvable use across core.* and unity.* — generic
        // definitions in a closed form.
        private static readonly string[] s_allTypeUses =
        {
            "core.String", "core.Int", "core.Float", "core.Bool", "core.Json",
            "core.List<core.String>", "core.List<core.List<core.Int>>",
            "unity.AssetPath", "unity.TypeName", "unity.HierarchyPath",
            "unity.Vector2", "unity.Vector3", "unity.Vector4",
            "unity.Quaternion", "unity.Euler", "unity.Color",
            "unity.Object", "unity.Component", "unity.GameObject",
            "unity.Transform", "unity.Animator", "unity.Material",
            "unity.Texture2D", "unity.MonoScript", "unity.ScriptableObject",
            "unity.AnimatorController",
            "unity.ObjectRef", "unity.SceneRef", "unity.GameObjectRef",
            "unity.PrefabInstanceRef", "unity.ComponentRef<unity.Animator>",
            "unity.ComponentRef<type:MyGame.PlayerController>",
            "unity.AssetRef<unity.Texture2D>", "unity.PrefabAssetRef",
        };

        // ── SPEC §B8 keyword subset ─────────────────────────────────────────

        [Test]
        public void TheAuditedKeywordSetIsTheSpecB8List()
        {
            // Every subset assertion below compares against this set, so the
            // set itself is only evidence while it equals SPEC §B8's draft
            // 2020-12 list — an added or dropped member would silently widen
            // or narrow every "no keyword outside §B8" claim.
            CollectionAssert.AreEquivalent(
                new[]
                {
                    "type", "properties", "required", "additionalProperties",
                    "items", "enum", "pattern", "minimum", "maximum",
                    "maxLength", "description", "$ref", "$defs",
                },
                TypeSchemas.AllowedKeywords);
            Assert.AreEqual("#/$defs/", TypeSchemas.DefsRefPrefix);
        }

        [Test]
        public void EveryStandardTypeEmitsOnlyTheB8KeywordSubset()
        {
            Assert.AreEqual(0, s_build.Failures.Count);
            foreach (var typeId in s_allTypeUses)
            {
                var schema = Schema(typeId, s_sweepConstraints);
                AssertKeywordsAreB8Subset(schema.ToToken());
            }
        }

        // ── Fragment shapes and pinned canonical bytes ──────────────────────

        [Test]
        public void CoreFragmentsMatchTheirJsonKinds()
        {
            Assert.AreEqual(
                "{\"description\":\"JSON string.\",\"type\":\"string\"}",
                Schema("core.String").CanonicalText);
            Assert.AreEqual(
                "{\"description\":\"JSON integer (64-bit).\",\"type\":\"integer\"}",
                Schema("core.Int").CanonicalText);
            Assert.AreEqual(
                "{\"description\":\"JSON number (finite binary64); integer tokens convert to Double.\",\"type\":\"number\"}",
                Schema("core.Float").CanonicalText);
            Assert.AreEqual(
                "{\"description\":\"JSON boolean.\",\"type\":\"boolean\"}",
                Schema("core.Bool").CanonicalText);
            // core.Json admits every JSON value: an empty fragment.
            Assert.AreEqual(
                "{\"description\":\"Any JSON value; keeps the written tokens and admits explicit null.\"}",
                Schema("core.Json").CanonicalText);
        }

        [Test]
        public void Vector3FragmentIsThePinnedGoldenForm()
        {
            const string expected =
                "{\"additionalProperties\":false," +
                "\"description\":\"Vector3 as {\\\"x\\\":0,\\\"y\\\":0,\\\"z\\\":0}; components are Double positions.\"," +
                "\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"}}," +
                "\"required\":[\"x\",\"y\",\"z\"],\"type\":\"object\"}";
            var schema = Schema("unity.Vector3");
            Assert.AreEqual(expected, schema.CanonicalText);
            AssertKeywordsAreB8Subset(schema.ToToken());
        }

        [Test]
        public void ColorFragmentUnionsItsTwoSpellings()
        {
            const string expected =
                "{\"additionalProperties\":false," +
                "\"description\":\"Color as {\\\"r\\\":1,\\\"g\\\":1,\\\"b\\\":1,\\\"a\\\":1} or \\\"#RRGGBBAA\\\".\"," +
                "\"pattern\":\"^#[0-9A-Fa-f]{8}(?![\\\\s\\\\S])\"," +
                "\"properties\":{\"a\":{\"type\":\"number\"},\"b\":{\"type\":\"number\"},\"g\":{\"type\":\"number\"},\"r\":{\"type\":\"number\"}}," +
                "\"required\":[\"r\",\"g\",\"b\",\"a\"],\"type\":[\"object\",\"string\"]}";
            Assert.AreEqual(expected, Schema("unity.Color").CanonicalText);
        }

        [Test]
        public void ClassIdFragmentRejectsEveryValue()
        {
            Assert.AreEqual(
                "{\"description\":\"Built-in class ID for UnityEngine.GameObject; carries no JSON value.\",\"enum\":[]}",
                Schema("unity.GameObject").CanonicalText);
        }

        // ── $defs and $ref (SPEC §B8) ───────────────────────────────────────

        [Test]
        public void ListElementTypeIsReferencedThroughDefs()
        {
            const string expected =
                "{\"$defs\":{\"unity.Vector3\":{" +
                "\"additionalProperties\":false," +
                "\"description\":\"Vector3 as {\\\"x\\\":0,\\\"y\\\":0,\\\"z\\\":0}; components are Double positions.\"," +
                "\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"}}," +
                "\"required\":[\"x\",\"y\",\"z\"],\"type\":\"object\"}}," +
                "\"description\":\"JSON array whose elements have the element type.\"," +
                "\"items\":{\"$ref\":\"#/$defs/unity.Vector3\"},\"type\":\"array\"}";
            Assert.AreEqual(expected, Schema("core.List<unity.Vector3>").CanonicalText);
        }

        [Test]
        public void NestedListCollectsEveryReferencedType()
        {
            var schema = Schema("core.List<core.List<core.Int>>").ToToken();
            var defs = (JObject)schema["$defs"]!;
            Assert.IsNotNull(defs["core.List<core.Int>"]);
            Assert.IsNotNull(defs["core.Int"]);
            Assert.AreEqual(
                "#/$defs/core.List<core.Int>",
                (string)schema["items"]!["$ref"]!);
            Assert.AreEqual(
                "#/$defs/core.Int",
                (string)defs["core.List<core.Int>"]!["items"]!["$ref"]!);
            Assert.AreEqual("integer", (string)defs["core.Int"]!["type"]!);
            AssertKeywordsAreB8Subset(schema);
        }

        [Test]
        public void ComponentRefOwnerPointsIntoDefs()
        {
            var schema = Schema("unity.ComponentRef<unity.Animator>").ToToken();
            var properties = (JObject)schema["properties"]!;
            Assert.AreEqual(
                "#/$defs/unity.GameObjectRef",
                (string)properties["owner"]!["$ref"]!);
            var defs = (JObject)schema["$defs"]!;
            var owner = (JObject)defs["unity.GameObjectRef"]!;
            Assert.AreEqual("object", (string)owner["type"]!);
            var kindEnum = (JArray)owner["properties"]!["kind"]!["enum"]!;
            Assert.AreEqual(2, kindEnum.Count);
            Assert.AreEqual("gameObject", (string)kindEnum[0]!);
            Assert.AreEqual("prefabInstance", (string)kindEnum[1]!);
            AssertKeywordsAreB8Subset(schema);
        }

        [Test]
        public void UnreferencedTypeHasNoDefsMember()
        {
            Assert.IsNull(Schema("unity.SceneRef").ToToken()["$defs"]);
            Assert.IsNull(Schema("core.Int").ToToken()["$defs"]);
            Assert.IsNull(Schema("unity.PrefabAssetRef").ToToken()["$defs"]);
        }

        [Test]
        public void EveryEmittedRefResolvesToItsCanonicalDefsEntry()
        {
            // SPEC §B8 points a reference at the collected definition with
            // "$ref": "#/$defs/<TypeId>". The pinned documents show that form
            // for one fixture each; this sweep holds it for every fixture and
            // every emission variant: no pointer outside the #/$defs form, no
            // tail that is not the canonical Type ID, no dangling pointer and
            // no keyword outside §B8 once $defs, nullability, port
            // constraints or a description join the fragment.
            foreach (var typeId in s_allTypeUses)
            {
                foreach (var schema in new[]
                {
                    Schema(typeId),
                    Schema(typeId, s_sweepConstraints),
                    Schema(typeId, nullable: true),
                    Schema(typeId, description: "Port doc."),
                })
                {
                    var token = schema.ToToken();
                    var defs = token["$defs"] as JObject;
                    foreach (var reference in CollectSchemaRefs(token))
                    {
                        Assert.IsTrue(
                            reference.StartsWith(TypeSchemas.DefsRefPrefix, System.StringComparison.Ordinal),
                            typeId + ": $ref does not point into $defs: " + reference);
                        var tail = reference.Substring(TypeSchemas.DefsRefPrefix.Length);
                        var target = TypeRef.Parse(tail);
                        Assert.IsTrue(target.Ok, typeId + ": $ref tail is no Type ID: " + reference);
                        Assert.AreEqual(
                            target.Value!.ToString(), tail,
                            typeId + ": $ref tail is not the canonical Type ID: " + reference);
                        Assert.IsNotNull(
                            defs?[target.Value.ToString()],
                            typeId + ": $ref points at no $defs entry: " + reference);
                    }
                    AssertKeywordsAreB8Subset(token);
                }
            }
        }

        [Test]
        public void EachDefsEntryEqualsTheReferencedTypeFragment()
        {
            // The collected definitions are exactly the referenced types and
            // nothing else: every entry is named by a "$ref" of the document,
            // carries no nested "$defs" (its own references join the same
            // table) and is byte-for-byte the fragment Generate hands out for
            // that type alone — so a port's constraints and the port's
            // description never leak into a shared definition.
            foreach (var typeId in s_allTypeUses)
            {
                var token = Schema(typeId).ToToken();
                if (token["$defs"] is not JObject defs)
                {
                    continue;
                }
                var references = CollectSchemaRefs(token);
                foreach (var entry in defs.Properties())
                {
                    Assert.IsTrue(
                        references.Contains(TypeSchemas.DefsRefPrefix + entry.Name),
                        typeId + ": $defs entry no reference names: " + entry.Name);
                    Assert.IsNull(
                        entry.Value["$defs"],
                        typeId + ": $defs entry nests its own $defs: " + entry.Name);
                    var standalone = (JObject)Schema(entry.Name).ToToken();
                    standalone.Remove("$defs");
                    Assert.AreEqual(
                        JsonValue.From(standalone).CanonicalText,
                        JsonValue.From(entry.Value).CanonicalText,
                        typeId + ": $defs entry is not the type's own fragment: " + entry.Name);
                }
            }
        }

#if !UNITY_6000_5_OR_NEWER
        // ── Positive / negative values under the test-only validator ────────

        [Test]
        public void PrimitiveSchemasJudgeInstances()
        {
            var text = Schema("core.String");
            AssertValid(text, "\"x\"");
            AssertInvalid(text, "5");
            AssertInvalid(text, "null");

            var integer = Schema("core.Int");
            AssertValid(integer, "5");
            AssertValid(integer, "-9007199254740993");
            AssertInvalid(integer, "1.5");
            AssertInvalid(integer, "\"5\"");
            AssertInvalid(integer, "null");

            var number = Schema("core.Float");
            AssertValid(number, "1.5");
            AssertValid(number, "0");
            AssertInvalid(number, "\"0.5\"");
            AssertInvalid(number, "true");

            var boolean = Schema("core.Bool");
            AssertValid(boolean, "true");
            AssertInvalid(boolean, "1");
            AssertInvalid(boolean, "\"true\"");

            var any = Schema("core.Json");
            AssertValid(any, "null");
            AssertValid(any, "5");
            AssertValid(any, "\"x\"");
            AssertValid(any, "{\"a\":1}");
            AssertValid(any, "[1,2]");
        }

        [Test]
        public void UnityStringSchemasJudgeInstances()
        {
            var assetPath = Schema("unity.AssetPath");
            AssertValid(assetPath, "\"Assets/Scenes/Main.unity\"");
            AssertValid(assetPath, "\"Packages/com.opweave.workflow/x\"");
            AssertInvalid(assetPath, "\"assets/lower/x\"");
            AssertInvalid(assetPath, "\"Assetsx\"");
            AssertInvalid(assetPath, "5");

            var typeName = Schema("unity.TypeName");
            AssertValid(typeName, "\"UnityEngine.Animator\"");
            AssertInvalid(typeName, "\"A B\"");
            AssertInvalid(typeName, "\"A..B\"");

            var hierarchyPath = Schema("unity.HierarchyPath");
            AssertValid(hierarchyPath, "\"/Root/Child\"");
            AssertInvalid(hierarchyPath, "\"Root/Child\"");
            AssertInvalid(hierarchyPath, "\"/A//B\"");
        }

        // JSON string bodies of hierarchy paths: "\\\\/" is the JSON spelling
        // of the SPEC §G2 escape (the two characters '\' and '/'), so these
        // paths embed or end a GameObject name with a real '/'.
        private static readonly string[] s_escapedHierarchyPaths =
        {
            "/Player",                  // unchanged plain name
            "/Root/Child[1]",           // the '[n]' suffix stays ordinary text
            "/Foo\\\\/",                // name 'Foo/'
            "/A\\\\/B/C",               // 'A/B', then 'C'
            "/\\\\/",                   // the name '/'
            "/Root/Child\\\\//Leaf",    // 'Child/', then 'Leaf'
            "/A/\\\\//B",               // 'A', the name '/', 'B'
            "/A\\\\",                   // 'A\': a lone backslash is a name char
            "/A\\\\\\\\/B",             // 'A\/B': ordinary backslash + escape
            "/P\\n",                    // '\n' is a name character too
        };

        // JSON string bodies an unescaped or missing separator makes invalid.
        private static readonly string[] s_invalidHierarchyPaths =
        {
            "",                         // empty string
            "/",                        // the bare root
            "A/B",                      // no leading '/'
            "Player",
            "Root/Child",
            "//A",                      // empty first segment
            "/A//B",                    // empty middle segment
            "/A/",                      // trailing unescaped '/'
            "/A\\\\//",                 // 'A/' then an empty segment
        };

        [Test]
        public void HierarchyPathSchemaAgreesWithTheCodecOnEscapedSlashes()
        {
            // The exported §B8 pattern is ECMA-262-flavoured but must judge
            // every fixture exactly like the codec — the codec-side fixtures
            // of T-W1.4-08 assert the same verdicts through Validate.
            var schema = Schema("unity.HierarchyPath");
            var gameObject = Schema("unity.GameObjectRef");
            foreach (var path in s_escapedHierarchyPaths)
            {
                AssertValid(schema, "\"" + path + "\"");
                AssertValid(gameObject, "{\"kind\":\"gameObject\",\"path\":\"" + path + "\"}");
            }
            foreach (var path in s_invalidHierarchyPaths)
            {
                AssertInvalid(schema, "\"" + path + "\"");
                AssertInvalid(gameObject, "{\"kind\":\"gameObject\",\"path\":\"" + path + "\"}");
            }
        }

        [Test]
        public void VectorSchemasJudgeInstances()
        {
            var vector3 = Schema("unity.Vector3");
            AssertValid(vector3, "{\"x\":0,\"y\":1,\"z\":0}");
            AssertValid(vector3, "{\"x\":0.5,\"y\":-1e2,\"z\":0}");
            AssertInvalid(vector3, "{\"x\":1,\"y\":2}");                 // SPEC §B7 test 1 shape
            AssertInvalid(vector3, "{\"x\":0,\"y\":1,\"z\":0,\"w\":3}");
            AssertInvalid(vector3, "{\"x\":\"0\",\"y\":1,\"z\":0}");
            AssertInvalid(vector3, "[0,1,2]");
            AssertInvalid(vector3, "null");

            var vector2 = Schema("unity.Vector2");
            AssertValid(vector2, "{\"x\":0,\"y\":1}");
            AssertInvalid(vector2, "{\"x\":0,\"y\":1,\"z\":2}");

            var vector4 = Schema("unity.Vector4");
            AssertValid(vector4, "{\"x\":0,\"y\":1,\"z\":0,\"w\":1}");
            AssertInvalid(vector4, "{\"x\":0,\"y\":1,\"z\":0}");

            var quaternion = Schema("unity.Quaternion");
            AssertValid(quaternion, "{\"x\":0,\"y\":0,\"z\":0,\"w\":1}");
            AssertInvalid(quaternion, "{\"x\":0,\"y\":0,\"z\":0}");

            var euler = Schema("unity.Euler");
            AssertValid(euler, "{\"x\":0,\"y\":90,\"z\":0}");
            AssertInvalid(euler, "{\"x\":0,\"y\":90}");
        }

        [Test]
        public void ColorSchemaJudgesBothSpellings()
        {
            var color = Schema("unity.Color");
            AssertValid(color, "{\"r\":1,\"g\":1,\"b\":1,\"a\":1}");
            AssertValid(color, "{\"r\":0.5,\"g\":0,\"b\":0,\"a\":1}");
            AssertValid(color, "\"#FF0000FF\"");
            AssertValid(color, "\"#ff0000ff\"");
            AssertInvalid(color, "\"#F00\"");
            AssertInvalid(color, "\"#FF0000\"");
            AssertInvalid(color, "\"FF0000FF\"");
            AssertInvalid(color, "{\"r\":1,\"g\":1,\"b\":1}");
            AssertInvalid(color, "{\"r\":1,\"g\":1,\"b\":1,\"a\":1,\"x\":0}");
            AssertInvalid(color, "5");
            AssertInvalid(color, "true");
            AssertInvalid(color, "null");
        }

        [Test]
        public void RefSchemasJudgeInstances()
        {
            var scene = Schema("unity.SceneRef");
            AssertValid(scene, "{\"kind\":\"scene\",\"path\":\"Assets/S.unity\"}");
            AssertValid(scene, "{\"kind\":\"scene\",\"guid\":\"g\"}");
            AssertInvalid(scene, "{\"kind\":\"scene\",\"path\":\"/NotAsset\"}");
            AssertInvalid(scene, "{\"kind\":\"scene\",\"handle\":3}");
            AssertInvalid(scene, "{\"path\":\"Assets/S.unity\"}");
            AssertInvalid(scene, "{\"kind\":\"gameObject\",\"path\":\"/P\"}");

            var gameObject = Schema("unity.GameObjectRef");
            AssertValid(gameObject, "{\"kind\":\"gameObject\",\"path\":\"/Player\"}");
            AssertValid(gameObject, "{\"kind\":\"gameObject\",\"handle\":-1338}");
            AssertValid(gameObject, "{\"kind\":\"gameObject\",\"handle\":\"18446744073709550254\"}");
            AssertValid(gameObject, "{\"kind\":\"gameObject\",\"scene\":\"Assets/S.unity\"}");
            AssertValid(gameObject, "{\"kind\":\"gameObject\",\"scene\":{\"kind\":\"scene\",\"path\":\"Assets/S.unity\"}}");
            AssertValid(gameObject, "{\"kind\":\"prefabInstance\",\"path\":\"/P\",\"prefabAsset\":\"g\"}");
            AssertInvalid(gameObject, "{\"kind\":\"scene\",\"path\":\"Assets/S.unity\"}");
            AssertInvalid(gameObject, "{\"kind\":\"gameObject\",\"path\":\"Assets/S.unity\"}");
            AssertInvalid(gameObject, "{\"kind\":\"gameObject\",\"handle\":\"abc\"}");
            AssertInvalid(gameObject, "{\"kind\":\"gameObject\",\"handle\":1.5}");
            AssertInvalid(gameObject, "{\"kind\":\"gameObject\",\"bogus\":1}");
            AssertInvalid(gameObject, "{\"kind\":\"gameObject\",\"scene\":{\"kind\":\"asset\"}}");
            AssertInvalid(gameObject, "{\"kind\":\"gameObject\",\"scene\":5}");

            var prefabInstance = Schema("unity.PrefabInstanceRef");
            AssertValid(prefabInstance, "{\"kind\":\"prefabInstance\",\"path\":\"/P\",\"prefabAsset\":\"g\"}");
            AssertInvalid(prefabInstance, "{\"kind\":\"gameObject\",\"path\":\"/P\"}");

            var component = Schema("unity.ComponentRef<unity.Animator>");
            AssertValid(component, "{\"kind\":\"component\",\"owner\":{\"kind\":\"gameObject\",\"path\":\"/P\"},\"type\":\"unity.Animator\",\"index\":0}");
            AssertValid(component, "{\"kind\":\"component\",\"type\":\"type:MyGame.PC\"}");
            AssertValid(component, "{\"kind\":\"component\",\"type\":\"UnityEngine.Animator\"}");
            AssertInvalid(component, "{\"kind\":\"component\",\"index\":-1}");
            AssertInvalid(component, "{\"kind\":\"component\",\"index\":1.5}");
            AssertInvalid(component, "{\"kind\":\"component\",\"owner\":\"/P\"}");
            AssertInvalid(component, "{\"kind\":\"component\",\"owner\":{\"kind\":\"scene\"}}");
            AssertInvalid(component, "{\"kind\":\"component\",\"type\":\"not a class!!\"}");

            var asset = Schema("unity.AssetRef<unity.Texture2D>");
            AssertValid(asset, "{\"kind\":\"asset\",\"guid\":\"g\",\"localId\":123}");
            AssertValid(asset, "{\"kind\":\"asset\",\"path\":\"Assets/m.mat\"}");
            AssertValid(asset, "{\"kind\":\"prefabAsset\",\"guid\":\"g\"}");
            AssertInvalid(asset, "{\"kind\":\"asset\",\"localId\":\"x\"}");
            AssertInvalid(asset, "{\"kind\":\"asset\",\"path\":\"/NotAPath\"}");

            var prefabAsset = Schema("unity.PrefabAssetRef");
            AssertValid(prefabAsset, "{\"kind\":\"prefabAsset\",\"guid\":\"g\"}");
            AssertInvalid(prefabAsset, "{\"kind\":\"prefabAsset\",\"localId\":1}");
            AssertInvalid(prefabAsset, "{\"kind\":\"asset\",\"guid\":\"g\"}");

            var objectRef = Schema("unity.ObjectRef");
            AssertValid(objectRef, "{\"kind\":\"asset\",\"guid\":\"g\"}");
            AssertValid(objectRef, "{\"kind\":\"component\",\"owner\":{\"kind\":\"gameObject\",\"path\":\"/P\"}}");
            AssertInvalid(objectRef, "{\"kind\":\"bogus\"}");
            AssertInvalid(objectRef, "{\"kind\":\"component\",\"index\":-1}");
            AssertInvalid(objectRef, "5");
        }

        [Test]
        public void ClassIdSchemaRejectsEveryInstance()
        {
            var gameObjectClass = Schema("unity.GameObject");
            AssertInvalid(gameObjectClass, "\"x\"");
            AssertInvalid(gameObjectClass, "5");
            AssertInvalid(gameObjectClass, "null");
            AssertInvalid(gameObjectClass, "{}");
        }

        // SPEC §B8 test 1 over the whole fixture set: one table keyed by
        // exactly s_allTypeUses, so a fixture cannot lose its validator
        // coverage without the completeness assertion failing. The class-id
        // fixtures carry no JSON value, so they list negative values only;
        // core.Json admits every JSON value, so it lists positive ones only.
        private static readonly (string TypeId, string[] Valid, string[] Invalid)[]
            s_validatorCases =
        {
            ("core.String",
                new[] { "\"x\"" },
                new[] { "5", "null" }),
            ("core.Int",
                new[] { "5", "-9007199254740993" },
                new[] { "1.5", "\"5\"", "null" }),
            ("core.Float",
                new[] { "1.5", "0" },
                new[] { "\"0.5\"", "true" }),
            ("core.Bool",
                new[] { "true" },
                new[] { "1", "\"true\"" }),
            ("core.Json",
                new[] { "null", "5", "\"x\"", "{\"a\":1}", "[1,2]" },
                System.Array.Empty<string>()),
            ("core.List<core.String>",
                new[] { "[]", "[\"a\",\"b\"]" },
                new[] { "[1]", "[null]", "[\"a\",1]", "\"a\"", "{\"a\":1}" }),
            ("core.List<core.List<core.Int>>",
                new[] { "[]", "[[1,2],[]]", "[[-3]]" },
                new[] { "[[1],2]", "[[1],null]", "[[\"1\"]]", "[\"x\"]" }),
            ("unity.AssetPath",
                new[] { "\"Assets/Scenes/Main.unity\"", "\"Packages/com.opweave.workflow/x\"" },
                new[] { "\"assets/lower/x\"", "\"Assetsx\"", "5" }),
            ("unity.TypeName",
                new[] { "\"UnityEngine.Animator\"" },
                new[] { "\"A B\"", "\"A..B\"" }),
            ("unity.HierarchyPath",
                new[] { "\"/Root/Child\"" },
                new[] { "\"Root/Child\"", "\"/A//B\"" }),
            ("unity.Vector2",
                new[] { "{\"x\":0,\"y\":1}" },
                new[] { "{\"x\":0,\"y\":1,\"z\":2}" }),
            ("unity.Vector3",
                new[] { "{\"x\":0,\"y\":1,\"z\":0}", "{\"x\":0.5,\"y\":-1e2,\"z\":0}" },
                new[]
                {
                    "{\"x\":1,\"y\":2}", "{\"x\":0,\"y\":1,\"z\":0,\"w\":3}",
                    "{\"x\":\"0\",\"y\":1,\"z\":0}", "[0,1,2]", "null",
                }),
            ("unity.Vector4",
                new[] { "{\"x\":0,\"y\":1,\"z\":0,\"w\":1}" },
                new[] { "{\"x\":0,\"y\":1,\"z\":0}" }),
            ("unity.Quaternion",
                new[] { "{\"x\":0,\"y\":0,\"z\":0,\"w\":1}" },
                new[] { "{\"x\":0,\"y\":0,\"z\":0}" }),
            ("unity.Euler",
                new[] { "{\"x\":0,\"y\":90,\"z\":0}" },
                new[] { "{\"x\":0,\"y\":90}" }),
            ("unity.Color",
                new[] { "{\"r\":1,\"g\":1,\"b\":1,\"a\":1}", "\"#FF0000FF\"", "\"#ff0000ff\"" },
                new[]
                {
                    "\"#F00\"", "\"#FF0000\"", "\"FF0000FF\"", "{\"r\":1,\"g\":1,\"b\":1}",
                    "{\"r\":1,\"g\":1,\"b\":1,\"a\":1,\"x\":0}", "5", "true", "null",
                }),
            ("unity.Object",
                System.Array.Empty<string>(),
                new[] { "\"x\"", "5", "null", "{}", "[]", "true", "{\"a\":1}" }),
            ("unity.Component",
                System.Array.Empty<string>(),
                new[] { "\"x\"", "5", "null", "{}", "[]", "true", "{\"a\":1}" }),
            ("unity.GameObject",
                System.Array.Empty<string>(),
                new[] { "\"x\"", "5", "null", "{}", "[]", "true", "{\"a\":1}" }),
            ("unity.Transform",
                System.Array.Empty<string>(),
                new[] { "\"x\"", "5", "null", "{}", "[]", "true", "{\"a\":1}" }),
            ("unity.Animator",
                System.Array.Empty<string>(),
                new[] { "\"x\"", "5", "null", "{}", "[]", "true", "{\"a\":1}" }),
            ("unity.Material",
                System.Array.Empty<string>(),
                new[] { "\"x\"", "5", "null", "{}", "[]", "true", "{\"a\":1}" }),
            ("unity.Texture2D",
                System.Array.Empty<string>(),
                new[] { "\"x\"", "5", "null", "{}", "[]", "true", "{\"a\":1}" }),
            ("unity.MonoScript",
                System.Array.Empty<string>(),
                new[] { "\"x\"", "5", "null", "{}", "[]", "true", "{\"a\":1}" }),
            ("unity.ScriptableObject",
                System.Array.Empty<string>(),
                new[] { "\"x\"", "5", "null", "{}", "[]", "true", "{\"a\":1}" }),
            ("unity.AnimatorController",
                System.Array.Empty<string>(),
                new[] { "\"x\"", "5", "null", "{}", "[]", "true", "{\"a\":1}" }),
            ("unity.ObjectRef",
                new[]
                {
                    "{\"kind\":\"asset\",\"guid\":\"g\"}",
                    "{\"kind\":\"component\",\"owner\":{\"kind\":\"gameObject\",\"path\":\"/P\"}}",
                },
                new[] { "{\"kind\":\"bogus\"}", "{\"kind\":\"component\",\"index\":-1}", "5" }),
            ("unity.SceneRef",
                new[] { "{\"kind\":\"scene\",\"path\":\"Assets/S.unity\"}", "{\"kind\":\"scene\",\"guid\":\"g\"}" },
                new[]
                {
                    "{\"kind\":\"scene\",\"path\":\"/NotAsset\"}", "{\"kind\":\"scene\",\"handle\":3}",
                    "{\"path\":\"Assets/S.unity\"}", "{\"kind\":\"gameObject\",\"path\":\"/P\"}",
                }),
            ("unity.GameObjectRef",
                new[]
                {
                    "{\"kind\":\"gameObject\",\"path\":\"/Player\"}",
                    "{\"kind\":\"gameObject\",\"handle\":\"18446744073709550254\"}",
                    "{\"kind\":\"prefabInstance\",\"path\":\"/P\",\"prefabAsset\":\"g\"}",
                },
                new[]
                {
                    "{\"kind\":\"scene\",\"path\":\"Assets/S.unity\"}",
                    "{\"kind\":\"gameObject\",\"path\":\"Assets/S.unity\"}",
                    "{\"kind\":\"gameObject\",\"handle\":\"abc\"}",
                    "{\"kind\":\"gameObject\",\"bogus\":1}",
                }),
            ("unity.PrefabInstanceRef",
                new[] { "{\"kind\":\"prefabInstance\",\"path\":\"/P\",\"prefabAsset\":\"g\"}" },
                new[] { "{\"kind\":\"gameObject\",\"path\":\"/P\"}" }),
            ("unity.ComponentRef<unity.Animator>",
                new[]
                {
                    "{\"kind\":\"component\",\"owner\":{\"kind\":\"gameObject\",\"path\":\"/P\"}," +
                    "\"type\":\"unity.Animator\",\"index\":0}",
                    "{\"kind\":\"component\",\"type\":\"type:MyGame.PC\"}",
                },
                new[]
                {
                    "{\"kind\":\"component\",\"index\":-1}",
                    "{\"kind\":\"component\",\"owner\":\"/P\"}",
                    "{\"kind\":\"component\",\"type\":\"not a class!!\"}",
                }),
            ("unity.ComponentRef<type:MyGame.PlayerController>",
                new[]
                {
                    "{\"kind\":\"component\"}",
                    "{\"kind\":\"component\",\"type\":\"type:MyGame.PlayerController\"," +
                    "\"owner\":{\"kind\":\"gameObject\",\"path\":\"/P\"},\"index\":0}",
                },
                new[]
                {
                    "{\"kind\":\"component\",\"index\":-1}",
                    "{\"kind\":\"component\",\"owner\":{\"kind\":\"scene\"}}",
                    "{\"kind\":\"gameObject\",\"path\":\"/P\"}",
                    "{\"kind\":\"component\",\"type\":\"MyGame Player\"}",
                }),
            ("unity.AssetRef<unity.Texture2D>",
                new[]
                {
                    "{\"kind\":\"asset\",\"guid\":\"g\",\"localId\":123}",
                    "{\"kind\":\"asset\",\"path\":\"Assets/m.mat\"}",
                    "{\"kind\":\"prefabAsset\",\"guid\":\"g\"}",
                },
                new[] { "{\"kind\":\"asset\",\"localId\":\"x\"}", "{\"kind\":\"asset\",\"path\":\"/NotAPath\"}" }),
            ("unity.PrefabAssetRef",
                new[] { "{\"kind\":\"prefabAsset\",\"guid\":\"g\"}" },
                new[] { "{\"kind\":\"prefabAsset\",\"localId\":1}", "{\"kind\":\"asset\",\"guid\":\"g\"}" }),
        };

        [Test]
        public void EveryFixtureJudgesItsPositiveAndNegativeInstances()
        {
            CollectionAssert.AreEquivalent(
                s_allTypeUses,
                s_validatorCases.Select(item => item.TypeId).ToArray());
            foreach (var (typeId, valid, invalid) in s_validatorCases)
            {
                var schema = Schema(typeId);
                foreach (var instance in valid)
                {
                    AssertValid(schema, instance);
                }
                foreach (var instance in invalid)
                {
                    AssertInvalid(schema, instance);
                }
            }
        }

        // ── PortConstraints → enum/pattern/minimum/maximum/maxLength ────────

        [Test]
        public void ConstraintsMapToTheB8Keywords()
        {
            var constrained = Schema(
                "core.String",
                new PortConstraints(new[] { "a", "b", "abcd" }, "^a", null, null, 3));
            var token = constrained.ToToken();
            Assert.AreEqual("a", (string)token["enum"]![0]!);
            Assert.AreEqual("^a", (string)token["pattern"]!);
            Assert.AreEqual(3, (int)token["maxLength"]!);
            AssertValid(constrained, "\"a\"");
            AssertInvalid(constrained, "\"b\"");      // enum member, pattern fails
            AssertInvalid(constrained, "\"c\"");      // enum fails
            AssertInvalid(constrained, "\"abcd\"");   // enum member and pattern, maxLength fails
        }

        [Test]
        public void MaxLengthCountsUnicodeCodePoints()
        {
            // Draft 2020-12 maxLength counts Unicode code points: one emoji
            // — a surrogate pair in UTF-16, two in .NET string.Length — is
            // length 1, the same boundary the Codec applies
            // (CoreCodecTests.MaxLengthCountsUnicodeCodePoints).
            var constrained = Schema(
                "core.String", new PortConstraints(null, null, null, null, 1));
            AssertValid(constrained, "\"\\ud83d\\ude00\"");
            AssertInvalid(constrained, "\"\\ud83d\\ude00\\ud83d\\ude01\"");
            // BMP boundaries are unchanged.
            AssertValid(constrained, "\"a\"");
            AssertInvalid(constrained, "\"ab\"");
        }

        [Test]
        public void NumericBoundsMapToMinimumAndMaximum()
        {
            var bounded = Schema(
                "core.Float", new PortConstraints(null, null, 1.0, 5.0, null));
            var token = bounded.ToToken();
            Assert.AreEqual("1e0", JsonValue.From(token["minimum"]!).CanonicalText);
            Assert.AreEqual("5e0", JsonValue.From(token["maximum"]!).CanonicalText);
            AssertValid(bounded, "3");
            AssertValid(bounded, "1");
            AssertInvalid(bounded, "0");
            AssertInvalid(bounded, "7");
        }

        [Test]
        public void PortPatternConjoinsWithTheTypePattern()
        {
            var constrained = Schema(
                "unity.AssetPath", new PortConstraints(null, "foo", null, null, null));
            Assert.AreEqual(
                @"^(?=[\s\S]*(?:foo))(Assets|Packages)/",
                (string)constrained.ToToken()["pattern"]!);
            AssertValid(constrained, "\"Assets/foo/x\"");
            AssertInvalid(constrained, "\"Assets/x\"");
            AssertInvalid(constrained, "\"foo/x\"");
        }

        [Test]
        public void PortEnumIntersectsWithTheCodecEnum()
        {
            // The class-id fragment's never-valid enum stays never-valid:
            // a port Enum may narrow, never widen (the Codec rejects all).
            var constrained = Schema(
                "unity.GameObject", new PortConstraints(new[] { "a" }, null, null, null, null));
            var enumArray = (JArray)constrained.ToToken()["enum"]!;
            Assert.AreEqual(0, enumArray.Count);
            AssertInvalid(constrained, "\"a\"");
            AssertInvalid(constrained, "null");
        }

        [Test]
        public void PortBoundsIntersectWithTheDeclaredSchema()
        {
            var built = BuildAcmeRegistry();

            // Port bounds weaker than the declared schema keep the declared
            // bound: a port constraint narrows the type's shape, it can
            // never widen it (the Codec-side bound still applies).
            var weaker = TypeSchemas.Generate(
                built.Registry, Type("acme.Ranged"),
                new PortConstraints(null, null, 0.0, 1000.0, null));
            Assert.IsTrue(weaker.Ok);
            var weakerToken = weaker.Value!.ToToken();
            Assert.AreEqual(10, (int)weakerToken["minimum"]!);
            Assert.AreEqual(100, (int)weakerToken["maximum"]!);
            AssertValid(weaker.Value!, "10");
            AssertValid(weaker.Value!, "100");
            AssertInvalid(weaker.Value!, "5");
            AssertInvalid(weaker.Value!, "150");

            // Port bounds stricter than the declared schema narrow it.
            var stricter = TypeSchemas.Generate(
                built.Registry, Type("acme.Ranged"),
                new PortConstraints(null, null, 50.0, 60.0, null));
            Assert.IsTrue(stricter.Ok);
            var stricterToken = stricter.Value!.ToToken();
            Assert.AreEqual("5e1", JsonValue.From(stricterToken["minimum"]!).CanonicalText);
            Assert.AreEqual("6e1", JsonValue.From(stricterToken["maximum"]!).CanonicalText);
            AssertValid(stricter.Value!, "55");
            AssertInvalid(stricter.Value!, "40");
            AssertInvalid(stricter.Value!, "70");

            // maxLength keeps the smaller of the declared and port bounds.
            var loose = TypeSchemas.Generate(
                built.Registry, Type("acme.Capped"),
                new PortConstraints(null, null, null, null, 10));
            Assert.IsTrue(loose.Ok);
            Assert.AreEqual(4, (int)loose.Value!.ToToken()["maxLength"]!);
            AssertValid(loose.Value!, "\"abcd\"");
            AssertInvalid(loose.Value!, "\"abcde\"");

            var tight = TypeSchemas.Generate(
                built.Registry, Type("acme.Capped"),
                new PortConstraints(null, null, null, null, 2));
            Assert.IsTrue(tight.Ok);
            Assert.AreEqual(2, (int)tight.Value!.ToToken()["maxLength"]!);
            AssertValid(tight.Value!, "\"ab\"");
            AssertInvalid(tight.Value!, "\"abc\"");
        }

        // ── SPEC §B0.1/§A5 nullable flag ────────────────────────────────────

        [Test]
        public void NullableAdmitsExplicitNull()
        {
            var nullable = Schema("core.Int", nullable: true);
            Assert.AreEqual(
                "[\"integer\",\"null\"]",
                JsonValue.From((JArray)nullable.ToToken()["type"]!).CanonicalText);
            AssertValid(nullable, "null");
            AssertValid(nullable, "5");
            AssertInvalid(nullable, "\"x\"");

            var nullableClass = Schema("unity.GameObject", nullable: true);
            AssertValid(nullableClass, "null");
            AssertInvalid(nullableClass, "\"x\"");

            var nullableConstrained = Schema(
                "core.String", new PortConstraints(new[] { "a" }, null, null, null, null), nullable: true);
            AssertValid(nullableConstrained, "null");
            AssertValid(nullableConstrained, "\"a\"");
            AssertInvalid(nullableConstrained, "\"b\"");
        }

        [Test]
        public void NullableAdmitsNullThroughARootRef()
        {
            // A provider-declared fragment that is a root $ref applies the
            // referenced schema to the instance, so widening only local
            // type/enum keywords cannot admit null — the referenced $defs
            // entry carries the explicit-null admission instead.
            var built = BuildAcmeRegistry();
            var aliased = TypeSchemas.Generate(
                built.Registry, Type("acme.Alias"), nullable: true);
            Assert.IsTrue(aliased.Ok);
            var token = aliased.Value!.ToToken();
            Assert.AreEqual("#/$defs/acme.Widget", (string)token["$ref"]!);
            var widget = (JObject)((JObject)token["$defs"]!)["acme.Widget"]!;
            Assert.AreEqual("null", (string)((JArray)widget["type"]!)[1]!);
            AssertKeywordsAreB8Subset(token);
            AssertValid(aliased.Value!, "null");
            AssertValid(aliased.Value!, "\"w1\"");
            AssertInvalid(aliased.Value!, "\"x\"");
            AssertInvalid(aliased.Value!, "5");

            // Without the flag the alias keeps rejecting explicit null.
            var plain = TypeSchemas.Generate(built.Registry, Type("acme.Alias"));
            Assert.IsTrue(plain.Ok);
            AssertInvalid(plain.Value!, "null");
            AssertValid(plain.Value!, "\"w1\"");
        }
#endif

        // ── Description, fallback and byte stability ────────────────────────

        [Test]
        public void DescriptionFallsBackToTheTypeAndHonoursAnOverride()
        {
            Assert.AreEqual(
                "JSON string.",
                (string)Schema("core.String").ToToken()["description"]!);
            Assert.AreEqual(
                "Port doc.",
                (string)Schema("core.String", description: "Port doc.").ToToken()["description"]!);
        }

#if !UNITY_6000_5_OR_NEWER
        [Test]
        public void ExternalCodecYieldsTheUnconstrainedFragment()
        {
            // An external Codec cannot implement the internal schema
            // contract, so a definition that declares no Schema gets the
            // fragment {} plus port constraints and description — an honest
            // "shape unknown to Core".
            var built = BuildAcmeRegistry();
            var result = TypeSchemas.Generate(
                built.Registry,
                Type("acme.Opaque"),
                new PortConstraints(new[] { "a" }, null, null, null, null));
            Assert.IsTrue(result.Ok);
            Assert.AreEqual(
                "{\"description\":\"an opaque type\",\"enum\":[\"a\"]}",
                result.Value!.CanonicalText);
            AssertValid(result.Value!, "\"a\"");
            AssertInvalid(result.Value!, "\"b\"");
        }

        // ── TypeDefinition.Schema: the external provider's §B8 fragment ────

        [Test]
        public void ExternalTypeEmitsItsDeclaredSchema()
        {
            var built = BuildAcmeRegistry();
            var result = TypeSchemas.Generate(built.Registry, Type("acme.Widget"));
            Assert.IsTrue(result.Ok);
            var token = result.Value!.ToToken();
            Assert.AreEqual("string", (string)token["type"]!);
            Assert.AreEqual(@"^w[0-9]+(?![\s\S])", (string)token["pattern"]!);
            Assert.AreEqual("a test type", (string)token["description"]!);
            AssertKeywordsAreB8Subset(token);
            AssertValid(result.Value!, "\"w42\"");
            AssertInvalid(result.Value!, "\"x42\"");
            AssertInvalid(result.Value!, "\"w\"");
            AssertInvalid(result.Value!, "\"w42\\n\"");
            AssertInvalid(result.Value!, "5");
        }

        [Test]
        public void ExternalTypeSchemaStillTakesPortConstraintsAndNullable()
        {
            var built = BuildAcmeRegistry();
            var constrained = TypeSchemas.Generate(
                built.Registry,
                Type("acme.Widget"),
                new PortConstraints(new[] { "w1", "w9" }, null, null, null, null),
                nullable: true);
            Assert.IsTrue(constrained.Ok);
            var token = constrained.Value!.ToToken();
            Assert.AreEqual("w1", (string)((JArray)token["enum"]!)[0]!);
            AssertValid(constrained.Value!, "\"w1\"");
            AssertInvalid(constrained.Value!, "\"w2\"");
            AssertValid(constrained.Value!, "null");
        }

        [Test]
        public void ListOfExternalTypeReferencesTheProvidedSchema()
        {
            var built = BuildAcmeRegistry();
            var result = TypeSchemas.Generate(
                built.Registry, Type("core.List<acme.Widget>"));
            Assert.IsTrue(result.Ok);
            var token = result.Value!.ToToken();
            Assert.AreEqual(
                "#/$defs/acme.Widget",
                (string)token["items"]!["$ref"]!);
            var widget = (JObject)((JObject)token["$defs"]!)["acme.Widget"]!;
            Assert.AreEqual("string", (string)widget["type"]!);
            Assert.AreEqual(@"^w[0-9]+(?![\s\S])", (string)widget["pattern"]!);
            Assert.AreEqual("a test type", (string)widget["description"]!);
            AssertKeywordsAreB8Subset(token);
            AssertValid(result.Value!, "[]");
            AssertValid(result.Value!, "[\"w1\",\"w2\"]");
            AssertInvalid(result.Value!, "[\"w1\",\"x\"]");
            AssertInvalid(result.Value!, "[\"w1\",5]");
            AssertInvalid(result.Value!, "[null]");
        }

        [Test]
        public void ProviderSchemaRefsAreCollectedIntoDefs()
        {
            // acme.Box's declared fragment references acme.Widget through a
            // "$ref": the generator registers it and emits its definition.
            var built = BuildAcmeRegistry();
            var result = TypeSchemas.Generate(built.Registry, Type("acme.Box"));
            Assert.IsTrue(result.Ok);
            var token = result.Value!.ToToken();
            Assert.AreEqual(
                "#/$defs/acme.Widget",
                (string)token["properties"]!["w"]!["$ref"]!);
            var widget = (JObject)((JObject)token["$defs"]!)["acme.Widget"]!;
            Assert.AreEqual("string", (string)widget["type"]!);
            AssertKeywordsAreB8Subset(token);
            AssertValid(result.Value!, "{\"w\":\"w1\"}");
            AssertInvalid(result.Value!, "{\"w\":\"x\"}");
            AssertInvalid(result.Value!, "{}");
            AssertInvalid(result.Value!, "{\"w\":\"w1\",\"extra\":1}");
        }

        [Test]
        public void ProviderSchemaRefIsCanonicalized()
        {
            // A whitespace-tolerant Type ID spelling in "$ref" resolves to the
            // canonical $defs key the same way TypeRef.Parse normalizes it.
            var built = TypeRegistryBuilder.Build(new[]
            {
                new TypeProviderCandidate(
                    CoreTypeProvider.ProviderId, new CoreTypeProvider(), OperationOrigin.Standard),
                new TypeProviderCandidate(
                    "acme.widgets",
                    new LooseRefProvider(),
                    OperationOrigin.Custom),
            });
            Assert.AreEqual(0, built.Failures.Count);
            var result = TypeSchemas.Generate(
                built.Registry, Type("acme.LooseBox"));
            Assert.IsTrue(result.Ok);
            var token = result.Value!.ToToken();
            Assert.AreEqual(
                "#/$defs/core.List<core.Int>",
                (string)token["properties"]!["ns"]!["$ref"]!);
            var defs = (JObject)token["$defs"]!;
            Assert.IsNotNull(defs["core.List<core.Int>"]);
            // The referenced list's own element type lands in $defs too.
            Assert.IsNotNull(defs["core.Int"]);
            AssertValid(result.Value!, "{\"ns\":[1,2]}");
            AssertInvalid(result.Value!, "{\"ns\":[\"x\"]}");
        }

        // ── Provider Schema keyword-subset gate (SPEC §B8) ──────────────

        [Test]
        public void ProviderSchemaWithARootKeywordOutsideB8IsNotEmitted()
        {
            var built = BuildAcmeRegistry();
            // 'format' is outside the SPEC §B8 subset, so the declared
            // fragment is unusable: the type degrades to the schema-less {}
            // — an honest "shape unknown to Core" — with the description.
            var result = TypeSchemas.Generate(built.Registry, Type("acme.RootFormat"));
            Assert.IsTrue(result.Ok);
            Assert.AreEqual(
                "{\"description\":\"root keyword violation\"}",
                result.Value!.CanonicalText);
            AssertKeywordsAreB8Subset(result.Value!.ToToken());
            AssertValid(result.Value!, "\"w1\"");
            AssertValid(result.Value!, "5");
            AssertValid(result.Value!, "null");

            // The degraded fragment still takes port constraints and the
            // nullable flag, exactly like a schema-less external type.
            var constrained = TypeSchemas.Generate(
                built.Registry,
                Type("acme.RootFormat"),
                new PortConstraints(new[] { "a" }, null, null, null, null),
                nullable: true);
            Assert.IsTrue(constrained.Ok);
            Assert.AreEqual(
                "{\"description\":\"root keyword violation\",\"enum\":[\"a\",null]}",
                constrained.Value!.CanonicalText);
            AssertValid(constrained.Value!, "\"a\"");
            AssertValid(constrained.Value!, "null");
            AssertInvalid(constrained.Value!, "\"w1\"");
        }

        [Test]
        public void ProviderSchemaWithANestedKeywordOutsideB8IsNotEmitted()
        {
            var built = BuildAcmeRegistry();
            // 'format' nested inside a 'properties' member schema.
            var nested = TypeSchemas.Generate(built.Registry, Type("acme.NestedFormat"));
            Assert.IsTrue(nested.Ok);
            Assert.AreEqual(
                "{\"description\":\"nested keyword violation\"}",
                nested.Value!.CanonicalText);
            AssertKeywordsAreB8Subset(nested.Value!.ToToken());
            AssertValid(nested.Value!, "{\"v\":1}");
            AssertValid(nested.Value!, "5");

            // 'minLength' nested inside a private '$defs' member.
            var defs = TypeSchemas.Generate(built.Registry, Type("acme.DefsKeyword"));
            Assert.IsTrue(defs.Ok);
            Assert.AreEqual(
                "{\"description\":\"defs keyword violation\"}",
                defs.Value!.CanonicalText);

            // 'oneOf' nested inside the 'items' subschema.
            var items = TypeSchemas.Generate(built.Registry, Type("acme.ItemsKeyword"));
            Assert.IsTrue(items.Ok);
            Assert.AreEqual(
                "{\"description\":\"items keyword violation\"}",
                items.Value!.CanonicalText);
        }

        [Test]
        public void ProviderSchemaMemberNamesAreNotKeywords()
        {
            var built = BuildAcmeRegistry();
            // 'format' under 'properties' names a JSON member and 'oneOf'
            // under '$defs' names a private definition — both are data,
            // not schema keywords, so the fragment is emitted.
            var result = TypeSchemas.Generate(built.Registry, Type("acme.KeywordNamed"));
            Assert.IsTrue(result.Ok);
            var token = result.Value!.ToToken();
            Assert.AreEqual(
                "string", (string)token["properties"]!["format"]!["type"]!);
            Assert.AreEqual(
                "integer", (string)token["$defs"]!["oneOf"]!["type"]!);
            AssertKeywordsAreB8Subset(token);
            AssertValid(result.Value!, "{\"format\":\"x\"}");
            AssertInvalid(result.Value!, "{\"format\":5}");
        }

        [Test]
        public void ReferencedTypeWithAnInvalidSchemaDegradesInDefs()
        {
            var built = BuildAcmeRegistry();
            // acme.RefToInvalid's own fragment is valid and references
            // acme.NestedFormat, whose declared fragment is not: the $defs
            // entry degrades to the honest unconstrained shape — never the
            // violating fragment.
            var result = TypeSchemas.Generate(built.Registry, Type("acme.RefToInvalid"));
            Assert.IsTrue(result.Ok);
            var token = result.Value!.ToToken();
            var entry = (JObject)((JObject)token["$defs"]!)["acme.NestedFormat"]!;
            Assert.AreEqual(
                "{\"description\":\"nested keyword violation\"}",
                JsonValue.From(entry).CanonicalText);
            Assert.IsFalse(result.Value!.CanonicalText.Contains("\"format\""));
            AssertKeywordsAreB8Subset(token);
            AssertValid(result.Value!, "{\"v\":\"w1\"}");
            AssertValid(result.Value!, "{\"v\":5}");
        }

        [Test]
        public void ProviderSchemaInsideTheSubsetIsEmittedUnchanged()
        {
            var built = BuildAcmeRegistry();
            // Every member of acme.DeepShape's declared fragment is inside
            // the subset — nested object schemas, a private $defs member
            // and a $ref — so it emits verbatim plus the merged registered
            // definition and the description.
            var result = TypeSchemas.Generate(built.Registry, Type("acme.DeepShape"));
            Assert.IsTrue(result.Ok);
            var token = result.Value!.ToToken();
            Assert.AreEqual(
                "string",
                (string)token["properties"]!["extra"]!["additionalProperties"]!["type"]!);
            Assert.AreEqual(
                "#/$defs/acme.Widget",
                (string)token["properties"]!["xs"]!["items"]!["$ref"]!);
            var defs = (JObject)token["$defs"]!;
            Assert.AreEqual(
                "integer", (string)defs["pair"]!["properties"]!["n"]!["type"]!);
            Assert.AreEqual("string", (string)defs["acme.Widget"]!["type"]!);
            AssertKeywordsAreB8Subset(token);
            AssertValid(result.Value!, "{\"xs\":[\"w1\"],\"extra\":{\"k\":\"v\"}}");
            AssertInvalid(result.Value!, "{\"xs\":[\"x\"]}");
            AssertInvalid(result.Value!, "{\"xs\":[\"w1\"],\"extra\":{\"k\":5}}");
            AssertInvalid(result.Value!, "{\"xs\":[\"w1\"],\"more\":1}");
        }

        [Test]
        public void PortPatternAppliesOverANonStringDeclaredPattern()
        {
            var built = BuildAcmeRegistry();
            // A declared 'pattern' member that is not a string is no
            // pattern a validator honours — the port constraint applies
            // alone rather than conjoining with (or crashing on) it.
            var result = TypeSchemas.Generate(
                built.Registry,
                Type("acme.OddPattern"),
                new PortConstraints(null, "^a", null, null, null));
            Assert.IsTrue(result.Ok);
            Assert.AreEqual("^a", (string)result.Value!.ToToken()["pattern"]!);
            AssertValid(result.Value!, "\"a\"");
            AssertInvalid(result.Value!, "\"b\"");
        }

        [Test]
        public void StrictEndAnchorsRejectTrailingNewlines()
        {
            // The emitted pattern's strict end assertion must match the
            // codec-side \z exactly: under a .NET-based validator a $ would
            // also match immediately before a final '\n', letting values pass
            // the schema that the codec rejects.
            AssertInvalid(Schema("unity.Color"), "\"#FF0000FF\\n\"");
            AssertInvalid(Schema("unity.TypeName"), "\"UnityEngine.Animator\\n\"");
            AssertInvalid(
                Schema("unity.GameObjectRef"),
                "{\"kind\":\"gameObject\",\"handle\":\"123\\n\"}");
            AssertInvalid(
                Schema("unity.ComponentRef<unity.Animator>"),
                "{\"kind\":\"component\",\"type\":\"unity.Animator\\n\"}");

            // The hierarchy-path body [^\\/] admits '\n' itself, so codec and
            // schema agree that a newline inside a segment is part of the
            // name — the strict end only rejects what the body cannot
            // consume. These stay valid and pin the dialect agreement.
            AssertValid(Schema("unity.HierarchyPath"), "\"/Root/Child\\n\"");
            AssertValid(
                Schema("unity.GameObjectRef"),
                "{\"kind\":\"gameObject\",\"path\":\"/Player\\n\"}");
        }
#endif

        // Pinned CanonicalText for every generated fixture: each runtime
        // asserts the same expected bytes, which is what proves
        // byte-stability across the dotnet and Unity runs — the
        // two-generation loop in IdenticalInputYieldsIdenticalBytes only
        // proves determinism inside a single process.
        private static readonly Dictionary<string, string> s_pinnedCanonicalText =
            new Dictionary<string, string>
            {
                ["core.String"] = "{\"description\":\"JSON string.\",\"type\":\"string\"}",
                ["core.Int"] = "{\"description\":\"JSON integer (64-bit).\",\"type\":\"integer\"}",
                ["core.Float"] = "{\"description\":\"JSON number (finite binary64); integer tokens convert to Double.\",\"type\":\"number\"}",
                ["core.Bool"] = "{\"description\":\"JSON boolean.\",\"type\":\"boolean\"}",
                ["core.Json"] = "{\"description\":\"Any JSON value; keeps the written tokens and admits explicit null.\"}",
                ["core.List<core.String>"] = "{\"$defs\":{\"core.String\":{\"description\":\"JSON string.\",\"type\":\"string\"}},\"description\":\"JSON array whose elements have the element type.\",\"items\":{\"$ref\":\"#/$defs/core.String\"},\"type\":\"array\"}",
                ["core.List<core.List<core.Int>>"] = "{\"$defs\":{\"core.Int\":{\"description\":\"JSON integer (64-bit).\",\"type\":\"integer\"},\"core.List<core.Int>\":{\"description\":\"JSON array whose elements have the element type.\",\"items\":{\"$ref\":\"#/$defs/core.Int\"},\"type\":\"array\"}},\"description\":\"JSON array whose elements have the element type.\",\"items\":{\"$ref\":\"#/$defs/core.List<core.Int>\"},\"type\":\"array\"}",
                ["unity.AssetPath"] = "{\"description\":\"Asset or package path ('Assets/…' or 'Packages/…', '/' separated).\",\"pattern\":\"^(Assets|Packages)/\",\"type\":\"string\"}",
                ["unity.TypeName"] = "{\"description\":\"Fully qualified type name (e.g. 'UnityEngine.Animator').\",\"pattern\":\"^[A-Za-z][A-Za-z0-9_]*(\\\\.[A-Za-z][A-Za-z0-9_]*)*(?![\\\\s\\\\S])\",\"type\":\"string\"}",
                ["unity.HierarchyPath"] = "{\"description\":\"Scene hierarchy path ('/Root/Child').\",\"pattern\":\"^/(?:\\\\\\\\/|\\\\\\\\(?!/)|[^\\\\\\\\/])+(?:/(?:\\\\\\\\/|\\\\\\\\(?!/)|[^\\\\\\\\/])+)*(?![\\\\s\\\\S])\",\"type\":\"string\"}",
                ["unity.Vector2"] = "{\"additionalProperties\":false,\"description\":\"Vector2 as {\\\"x\\\":0,\\\"y\\\":0}; components are Double positions.\",\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"}},\"required\":[\"x\",\"y\"],\"type\":\"object\"}",
                ["unity.Vector3"] = "{\"additionalProperties\":false,\"description\":\"Vector3 as {\\\"x\\\":0,\\\"y\\\":0,\\\"z\\\":0}; components are Double positions.\",\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"}},\"required\":[\"x\",\"y\",\"z\"],\"type\":\"object\"}",
                ["unity.Vector4"] = "{\"additionalProperties\":false,\"description\":\"Vector4 as {\\\"x\\\":0,\\\"y\\\":0,\\\"z\\\":0,\\\"w\\\":0}; components are Double positions.\",\"properties\":{\"w\":{\"type\":\"number\"},\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"}},\"required\":[\"x\",\"y\",\"z\",\"w\"],\"type\":\"object\"}",
                ["unity.Quaternion"] = "{\"additionalProperties\":false,\"description\":\"Quaternion as {\\\"x\\\":0,\\\"y\\\":0,\\\"z\\\":0,\\\"w\\\":1}; components are Double positions.\",\"properties\":{\"w\":{\"type\":\"number\"},\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"}},\"required\":[\"x\",\"y\",\"z\",\"w\"],\"type\":\"object\"}",
                ["unity.Euler"] = "{\"additionalProperties\":false,\"description\":\"Euler rotation in degrees as {\\\"x\\\":0,\\\"y\\\":90,\\\"z\\\":0}; a distinct type from unity.Vector3.\",\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"}},\"required\":[\"x\",\"y\",\"z\"],\"type\":\"object\"}",
                ["unity.Color"] = "{\"additionalProperties\":false,\"description\":\"Color as {\\\"r\\\":1,\\\"g\\\":1,\\\"b\\\":1,\\\"a\\\":1} or \\\"#RRGGBBAA\\\".\",\"pattern\":\"^#[0-9A-Fa-f]{8}(?![\\\\s\\\\S])\",\"properties\":{\"a\":{\"type\":\"number\"},\"b\":{\"type\":\"number\"},\"g\":{\"type\":\"number\"},\"r\":{\"type\":\"number\"}},\"required\":[\"r\",\"g\",\"b\",\"a\"],\"type\":[\"object\",\"string\"]}",
                ["unity.Object"] = "{\"description\":\"Built-in class ID for UnityEngine.Object; carries no JSON value.\",\"enum\":[]}",
                ["unity.Component"] = "{\"description\":\"Built-in class ID for UnityEngine.Component; carries no JSON value.\",\"enum\":[]}",
                ["unity.GameObject"] = "{\"description\":\"Built-in class ID for UnityEngine.GameObject; carries no JSON value.\",\"enum\":[]}",
                ["unity.Transform"] = "{\"description\":\"Built-in class ID for UnityEngine.Transform; carries no JSON value.\",\"enum\":[]}",
                ["unity.Animator"] = "{\"description\":\"Built-in class ID for UnityEngine.Animator; carries no JSON value.\",\"enum\":[]}",
                ["unity.Material"] = "{\"description\":\"Built-in class ID for UnityEngine.Material; carries no JSON value.\",\"enum\":[]}",
                ["unity.Texture2D"] = "{\"description\":\"Built-in class ID for UnityEngine.Texture2D; carries no JSON value.\",\"enum\":[]}",
                ["unity.MonoScript"] = "{\"description\":\"Built-in class ID for UnityEditor.MonoScript; carries no JSON value.\",\"enum\":[]}",
                ["unity.ScriptableObject"] = "{\"description\":\"Built-in class ID for UnityEngine.ScriptableObject; carries no JSON value.\",\"enum\":[]}",
                ["unity.AnimatorController"] = "{\"description\":\"Built-in class ID for UnityEditor.Animations.AnimatorController; carries no JSON value.\",\"enum\":[]}",
                ["unity.ObjectRef"] = "{\"$defs\":{\"unity.GameObjectRef\":{\"additionalProperties\":false,\"description\":\"Reference to a scene GameObject: kind 'gameObject'/'prefabInstance', keys scene, path, globalId, handle.\",\"properties\":{\"globalId\":{\"type\":\"string\"},\"handle\":{\"pattern\":\"^-?[0-9]+(?![\\\\s\\\\S])\",\"type\":[\"integer\",\"string\"]},\"kind\":{\"enum\":[\"gameObject\",\"prefabInstance\"],\"type\":\"string\"},\"path\":{\"pattern\":\"^/(?:\\\\\\\\/|\\\\\\\\(?!/)|[^\\\\\\\\/])+(?:/(?:\\\\\\\\/|\\\\\\\\(?!/)|[^\\\\\\\\/])+)*(?![\\\\s\\\\S])\",\"type\":\"string\"},\"prefabAsset\":{\"type\":\"string\"},\"scene\":{\"additionalProperties\":false,\"properties\":{\"guid\":{\"type\":\"string\"},\"kind\":{\"enum\":[\"scene\"],\"type\":\"string\"},\"path\":{\"pattern\":\"^(Assets|Packages)/\",\"type\":\"string\"}},\"required\":[\"kind\"],\"type\":[\"object\",\"string\"]}},\"required\":[\"kind\"],\"type\":\"object\"}},\"additionalProperties\":false,\"description\":\"Reference to a Unity object; 'kind' discriminates the actual Ref type.\",\"properties\":{\"globalId\":{\"type\":\"string\"},\"guid\":{\"type\":\"string\"},\"handle\":{\"pattern\":\"^-?[0-9]+(?![\\\\s\\\\S])\",\"type\":[\"integer\",\"string\"]},\"index\":{\"minimum\":0,\"type\":\"integer\"},\"kind\":{\"enum\":[\"scene\",\"gameObject\",\"prefabInstance\",\"component\",\"asset\",\"prefabAsset\"],\"type\":\"string\"},\"localId\":{\"type\":\"integer\"},\"owner\":{\"$ref\":\"#/$defs/unity.GameObjectRef\"},\"path\":{\"pattern\":\"^(Assets|Packages)/|^/(?:\\\\\\\\/|\\\\\\\\(?!/)|[^\\\\\\\\/])+(?:/(?:\\\\\\\\/|\\\\\\\\(?!/)|[^\\\\\\\\/])+)*(?![\\\\s\\\\S])\",\"type\":\"string\"},\"prefabAsset\":{\"type\":\"string\"},\"scene\":{\"additionalProperties\":false,\"properties\":{\"guid\":{\"type\":\"string\"},\"kind\":{\"enum\":[\"scene\"],\"type\":\"string\"},\"path\":{\"pattern\":\"^(Assets|Packages)/\",\"type\":\"string\"}},\"required\":[\"kind\"],\"type\":[\"object\",\"string\"]},\"type\":{\"pattern\":\"^(type:)?[A-Za-z][A-Za-z0-9_]*(\\\\.[A-Za-z][A-Za-z0-9_]*)*(?![\\\\s\\\\S])\",\"type\":\"string\"}},\"required\":[\"kind\"],\"type\":\"object\"}",
                ["unity.SceneRef"] = "{\"additionalProperties\":false,\"description\":\"Reference to a scene asset: kind 'scene', keys path, guid.\",\"properties\":{\"guid\":{\"type\":\"string\"},\"kind\":{\"enum\":[\"scene\"],\"type\":\"string\"},\"path\":{\"pattern\":\"^(Assets|Packages)/\",\"type\":\"string\"}},\"required\":[\"kind\"],\"type\":\"object\"}",
                ["unity.GameObjectRef"] = "{\"additionalProperties\":false,\"description\":\"Reference to a scene GameObject: kind 'gameObject'/'prefabInstance', keys scene, path, globalId, handle.\",\"properties\":{\"globalId\":{\"type\":\"string\"},\"handle\":{\"pattern\":\"^-?[0-9]+(?![\\\\s\\\\S])\",\"type\":[\"integer\",\"string\"]},\"kind\":{\"enum\":[\"gameObject\",\"prefabInstance\"],\"type\":\"string\"},\"path\":{\"pattern\":\"^/(?:\\\\\\\\/|\\\\\\\\(?!/)|[^\\\\\\\\/])+(?:/(?:\\\\\\\\/|\\\\\\\\(?!/)|[^\\\\\\\\/])+)*(?![\\\\s\\\\S])\",\"type\":\"string\"},\"prefabAsset\":{\"type\":\"string\"},\"scene\":{\"additionalProperties\":false,\"properties\":{\"guid\":{\"type\":\"string\"},\"kind\":{\"enum\":[\"scene\"],\"type\":\"string\"},\"path\":{\"pattern\":\"^(Assets|Packages)/\",\"type\":\"string\"}},\"required\":[\"kind\"],\"type\":[\"object\",\"string\"]}},\"required\":[\"kind\"],\"type\":\"object\"}",
                ["unity.PrefabInstanceRef"] = "{\"additionalProperties\":false,\"description\":\"Reference to a prefab instance root: kind 'prefabInstance', adds prefabAsset.\",\"properties\":{\"globalId\":{\"type\":\"string\"},\"handle\":{\"pattern\":\"^-?[0-9]+(?![\\\\s\\\\S])\",\"type\":[\"integer\",\"string\"]},\"kind\":{\"enum\":[\"prefabInstance\"],\"type\":\"string\"},\"path\":{\"pattern\":\"^/(?:\\\\\\\\/|\\\\\\\\(?!/)|[^\\\\\\\\/])+(?:/(?:\\\\\\\\/|\\\\\\\\(?!/)|[^\\\\\\\\/])+)*(?![\\\\s\\\\S])\",\"type\":\"string\"},\"prefabAsset\":{\"type\":\"string\"},\"scene\":{\"additionalProperties\":false,\"properties\":{\"guid\":{\"type\":\"string\"},\"kind\":{\"enum\":[\"scene\"],\"type\":\"string\"},\"path\":{\"pattern\":\"^(Assets|Packages)/\",\"type\":\"string\"}},\"required\":[\"kind\"],\"type\":[\"object\",\"string\"]}},\"required\":[\"kind\"],\"type\":\"object\"}",
                ["unity.ComponentRef<unity.Animator>"] = "{\"$defs\":{\"unity.GameObjectRef\":{\"additionalProperties\":false,\"description\":\"Reference to a scene GameObject: kind 'gameObject'/'prefabInstance', keys scene, path, globalId, handle.\",\"properties\":{\"globalId\":{\"type\":\"string\"},\"handle\":{\"pattern\":\"^-?[0-9]+(?![\\\\s\\\\S])\",\"type\":[\"integer\",\"string\"]},\"kind\":{\"enum\":[\"gameObject\",\"prefabInstance\"],\"type\":\"string\"},\"path\":{\"pattern\":\"^/(?:\\\\\\\\/|\\\\\\\\(?!/)|[^\\\\\\\\/])+(?:/(?:\\\\\\\\/|\\\\\\\\(?!/)|[^\\\\\\\\/])+)*(?![\\\\s\\\\S])\",\"type\":\"string\"},\"prefabAsset\":{\"type\":\"string\"},\"scene\":{\"additionalProperties\":false,\"properties\":{\"guid\":{\"type\":\"string\"},\"kind\":{\"enum\":[\"scene\"],\"type\":\"string\"},\"path\":{\"pattern\":\"^(Assets|Packages)/\",\"type\":\"string\"}},\"required\":[\"kind\"],\"type\":[\"object\",\"string\"]}},\"required\":[\"kind\"],\"type\":\"object\"}},\"additionalProperties\":false,\"description\":\"Reference to a component on a GameObject: kind 'component', keys owner, type, index.\",\"properties\":{\"index\":{\"minimum\":0,\"type\":\"integer\"},\"kind\":{\"enum\":[\"component\"],\"type\":\"string\"},\"owner\":{\"$ref\":\"#/$defs/unity.GameObjectRef\"},\"type\":{\"pattern\":\"^(type:)?[A-Za-z][A-Za-z0-9_]*(\\\\.[A-Za-z][A-Za-z0-9_]*)*(?![\\\\s\\\\S])\",\"type\":\"string\"}},\"required\":[\"kind\"],\"type\":\"object\"}",
                ["unity.ComponentRef<type:MyGame.PlayerController>"] = "{\"$defs\":{\"unity.GameObjectRef\":{\"additionalProperties\":false,\"description\":\"Reference to a scene GameObject: kind 'gameObject'/'prefabInstance', keys scene, path, globalId, handle.\",\"properties\":{\"globalId\":{\"type\":\"string\"},\"handle\":{\"pattern\":\"^-?[0-9]+(?![\\\\s\\\\S])\",\"type\":[\"integer\",\"string\"]},\"kind\":{\"enum\":[\"gameObject\",\"prefabInstance\"],\"type\":\"string\"},\"path\":{\"pattern\":\"^/(?:\\\\\\\\/|\\\\\\\\(?!/)|[^\\\\\\\\/])+(?:/(?:\\\\\\\\/|\\\\\\\\(?!/)|[^\\\\\\\\/])+)*(?![\\\\s\\\\S])\",\"type\":\"string\"},\"prefabAsset\":{\"type\":\"string\"},\"scene\":{\"additionalProperties\":false,\"properties\":{\"guid\":{\"type\":\"string\"},\"kind\":{\"enum\":[\"scene\"],\"type\":\"string\"},\"path\":{\"pattern\":\"^(Assets|Packages)/\",\"type\":\"string\"}},\"required\":[\"kind\"],\"type\":[\"object\",\"string\"]}},\"required\":[\"kind\"],\"type\":\"object\"}},\"additionalProperties\":false,\"description\":\"Reference to a component on a GameObject: kind 'component', keys owner, type, index.\",\"properties\":{\"index\":{\"minimum\":0,\"type\":\"integer\"},\"kind\":{\"enum\":[\"component\"],\"type\":\"string\"},\"owner\":{\"$ref\":\"#/$defs/unity.GameObjectRef\"},\"type\":{\"pattern\":\"^(type:)?[A-Za-z][A-Za-z0-9_]*(\\\\.[A-Za-z][A-Za-z0-9_]*)*(?![\\\\s\\\\S])\",\"type\":\"string\"}},\"required\":[\"kind\"],\"type\":\"object\"}",
                ["unity.AssetRef<unity.Texture2D>"] = "{\"additionalProperties\":false,\"description\":\"Reference to an asset of class T: kind 'asset'/'prefabAsset', keys guid, path, localId.\",\"properties\":{\"guid\":{\"type\":\"string\"},\"kind\":{\"enum\":[\"asset\",\"prefabAsset\"],\"type\":\"string\"},\"localId\":{\"type\":\"integer\"},\"path\":{\"pattern\":\"^(Assets|Packages)/\",\"type\":\"string\"}},\"required\":[\"kind\"],\"type\":\"object\"}",
                ["unity.PrefabAssetRef"] = "{\"additionalProperties\":false,\"description\":\"Reference to a prefab asset: kind 'prefabAsset', keys guid, path.\",\"properties\":{\"guid\":{\"type\":\"string\"},\"kind\":{\"enum\":[\"prefabAsset\"],\"type\":\"string\"},\"path\":{\"pattern\":\"^(Assets|Packages)/\",\"type\":\"string\"}},\"required\":[\"kind\"],\"type\":\"object\"}",
            };

        [Test]
        public void EveryFixtureMatchesItsPinnedCanonicalBytes()
        {
            Assert.AreEqual(s_allTypeUses.Length, s_pinnedCanonicalText.Count);
            foreach (var typeId in s_allTypeUses)
            {
                Assert.IsTrue(
                    s_pinnedCanonicalText.TryGetValue(typeId, out var expected),
                    "fixture missing from the pin table: " + typeId);
                Assert.AreEqual(expected, Schema(typeId).CanonicalText, typeId);
            }
        }

        [Test]
        public void IdenticalInputYieldsIdenticalBytes()
        {
            foreach (var typeId in s_allTypeUses)
            {
                var first = Schema(typeId, s_sweepConstraints);
                var second = Schema(typeId, s_sweepConstraints);
                Assert.AreEqual(first.CanonicalText, second.CanonicalText, typeId);
                Assert.AreEqual(first.Sha256, second.Sha256, typeId);
            }
        }

        // Pinned CanonicalText of the two emission variants the plain pin
        // table does not cover. One fixture per distinct emission path is
        // enough to pin, because the bytes are the assertion: a scalar type
        // keyword (core.String/Int/Float/Bool), the fragment that carries no
        // type at all (core.Json), a $defs entry with its $ref
        // (core.List<unity.Vector3>, unity.ComponentRef<unity.Animator>), a
        // codec pattern the port pattern conjoins with (unity.AssetPath,
        // unity.Color) and the never-valid class-id enum (unity.GameObject).
        // Both runtimes assert these same literals, which is what makes the
        // constrained and nullable forms cross-runtime byte-stable too.
        private static readonly Dictionary<string, (string Constrained, string Nullable)>
            s_pinnedVariantText =
            new Dictionary<string, (string Constrained, string Nullable)>
            {
                ["core.String"] = ("{\"description\":\"JSON string.\",\"enum\":[\"a\"],\"maxLength\":8,\"maximum\":9e0,\"minimum\":1e0,\"pattern\":\"^p\",\"type\":\"string\"}", "{\"description\":\"JSON string.\",\"type\":[\"string\",\"null\"]}"),
                ["core.Int"] = ("{\"description\":\"JSON integer (64-bit).\",\"enum\":[\"a\"],\"maxLength\":8,\"maximum\":9e0,\"minimum\":1e0,\"pattern\":\"^p\",\"type\":\"integer\"}", "{\"description\":\"JSON integer (64-bit).\",\"type\":[\"integer\",\"null\"]}"),
                ["core.Float"] = ("{\"description\":\"JSON number (finite binary64); integer tokens convert to Double.\",\"enum\":[\"a\"],\"maxLength\":8,\"maximum\":9e0,\"minimum\":1e0,\"pattern\":\"^p\",\"type\":\"number\"}", "{\"description\":\"JSON number (finite binary64); integer tokens convert to Double.\",\"type\":[\"number\",\"null\"]}"),
                ["core.Bool"] = ("{\"description\":\"JSON boolean.\",\"enum\":[\"a\"],\"maxLength\":8,\"maximum\":9e0,\"minimum\":1e0,\"pattern\":\"^p\",\"type\":\"boolean\"}", "{\"description\":\"JSON boolean.\",\"type\":[\"boolean\",\"null\"]}"),
                ["core.Json"] = ("{\"description\":\"Any JSON value; keeps the written tokens and admits explicit null.\",\"enum\":[\"a\"],\"maxLength\":8,\"maximum\":9e0,\"minimum\":1e0,\"pattern\":\"^p\"}", "{\"description\":\"Any JSON value; keeps the written tokens and admits explicit null.\"}"),
                ["core.List<unity.Vector3>"] = ("{\"$defs\":{\"unity.Vector3\":{\"additionalProperties\":false,\"description\":\"Vector3 as {\\\"x\\\":0,\\\"y\\\":0,\\\"z\\\":0}; components are Double positions.\",\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"}},\"required\":[\"x\",\"y\",\"z\"],\"type\":\"object\"}},\"description\":\"JSON array whose elements have the element type.\",\"enum\":[\"a\"],\"items\":{\"$ref\":\"#/$defs/unity.Vector3\"},\"maxLength\":8,\"maximum\":9e0,\"minimum\":1e0,\"pattern\":\"^p\",\"type\":\"array\"}", "{\"$defs\":{\"unity.Vector3\":{\"additionalProperties\":false,\"description\":\"Vector3 as {\\\"x\\\":0,\\\"y\\\":0,\\\"z\\\":0}; components are Double positions.\",\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"}},\"required\":[\"x\",\"y\",\"z\"],\"type\":\"object\"}},\"description\":\"JSON array whose elements have the element type.\",\"items\":{\"$ref\":\"#/$defs/unity.Vector3\"},\"type\":[\"array\",\"null\"]}"),
                ["unity.AssetPath"] = ("{\"description\":\"Asset or package path ('Assets/…' or 'Packages/…', '/' separated).\",\"enum\":[\"a\"],\"maxLength\":8,\"maximum\":9e0,\"minimum\":1e0,\"pattern\":\"^(?=[\\\\s\\\\S]*(?:^p))(Assets|Packages)/\",\"type\":\"string\"}", "{\"description\":\"Asset or package path ('Assets/…' or 'Packages/…', '/' separated).\",\"pattern\":\"^(Assets|Packages)/\",\"type\":[\"string\",\"null\"]}"),
                ["unity.Color"] = ("{\"additionalProperties\":false,\"description\":\"Color as {\\\"r\\\":1,\\\"g\\\":1,\\\"b\\\":1,\\\"a\\\":1} or \\\"#RRGGBBAA\\\".\",\"enum\":[\"a\"],\"maxLength\":8,\"maximum\":9e0,\"minimum\":1e0,\"pattern\":\"^(?=[\\\\s\\\\S]*(?:^p))#[0-9A-Fa-f]{8}(?![\\\\s\\\\S])\",\"properties\":{\"a\":{\"type\":\"number\"},\"b\":{\"type\":\"number\"},\"g\":{\"type\":\"number\"},\"r\":{\"type\":\"number\"}},\"required\":[\"r\",\"g\",\"b\",\"a\"],\"type\":[\"object\",\"string\"]}", "{\"additionalProperties\":false,\"description\":\"Color as {\\\"r\\\":1,\\\"g\\\":1,\\\"b\\\":1,\\\"a\\\":1} or \\\"#RRGGBBAA\\\".\",\"pattern\":\"^#[0-9A-Fa-f]{8}(?![\\\\s\\\\S])\",\"properties\":{\"a\":{\"type\":\"number\"},\"b\":{\"type\":\"number\"},\"g\":{\"type\":\"number\"},\"r\":{\"type\":\"number\"}},\"required\":[\"r\",\"g\",\"b\",\"a\"],\"type\":[\"object\",\"string\",\"null\"]}"),
                ["unity.GameObject"] = ("{\"description\":\"Built-in class ID for UnityEngine.GameObject; carries no JSON value.\",\"enum\":[],\"maxLength\":8,\"maximum\":9e0,\"minimum\":1e0,\"pattern\":\"^p\"}", "{\"description\":\"Built-in class ID for UnityEngine.GameObject; carries no JSON value.\",\"enum\":[null]}"),
                ["unity.ComponentRef<unity.Animator>"] = ("{\"$defs\":{\"unity.GameObjectRef\":{\"additionalProperties\":false,\"description\":\"Reference to a scene GameObject: kind 'gameObject'/'prefabInstance', keys scene, path, globalId, handle.\",\"properties\":{\"globalId\":{\"type\":\"string\"},\"handle\":{\"pattern\":\"^-?[0-9]+(?![\\\\s\\\\S])\",\"type\":[\"integer\",\"string\"]},\"kind\":{\"enum\":[\"gameObject\",\"prefabInstance\"],\"type\":\"string\"},\"path\":{\"pattern\":\"^/(?:\\\\\\\\/|\\\\\\\\(?!/)|[^\\\\\\\\/])+(?:/(?:\\\\\\\\/|\\\\\\\\(?!/)|[^\\\\\\\\/])+)*(?![\\\\s\\\\S])\",\"type\":\"string\"},\"prefabAsset\":{\"type\":\"string\"},\"scene\":{\"additionalProperties\":false,\"properties\":{\"guid\":{\"type\":\"string\"},\"kind\":{\"enum\":[\"scene\"],\"type\":\"string\"},\"path\":{\"pattern\":\"^(Assets|Packages)/\",\"type\":\"string\"}},\"required\":[\"kind\"],\"type\":[\"object\",\"string\"]}},\"required\":[\"kind\"],\"type\":\"object\"}},\"additionalProperties\":false,\"description\":\"Reference to a component on a GameObject: kind 'component', keys owner, type, index.\",\"enum\":[\"a\"],\"maxLength\":8,\"maximum\":9e0,\"minimum\":1e0,\"pattern\":\"^p\",\"properties\":{\"index\":{\"minimum\":0,\"type\":\"integer\"},\"kind\":{\"enum\":[\"component\"],\"type\":\"string\"},\"owner\":{\"$ref\":\"#/$defs/unity.GameObjectRef\"},\"type\":{\"pattern\":\"^(type:)?[A-Za-z][A-Za-z0-9_]*(\\\\.[A-Za-z][A-Za-z0-9_]*)*(?![\\\\s\\\\S])\",\"type\":\"string\"}},\"required\":[\"kind\"],\"type\":\"object\"}", "{\"$defs\":{\"unity.GameObjectRef\":{\"additionalProperties\":false,\"description\":\"Reference to a scene GameObject: kind 'gameObject'/'prefabInstance', keys scene, path, globalId, handle.\",\"properties\":{\"globalId\":{\"type\":\"string\"},\"handle\":{\"pattern\":\"^-?[0-9]+(?![\\\\s\\\\S])\",\"type\":[\"integer\",\"string\"]},\"kind\":{\"enum\":[\"gameObject\",\"prefabInstance\"],\"type\":\"string\"},\"path\":{\"pattern\":\"^/(?:\\\\\\\\/|\\\\\\\\(?!/)|[^\\\\\\\\/])+(?:/(?:\\\\\\\\/|\\\\\\\\(?!/)|[^\\\\\\\\/])+)*(?![\\\\s\\\\S])\",\"type\":\"string\"},\"prefabAsset\":{\"type\":\"string\"},\"scene\":{\"additionalProperties\":false,\"properties\":{\"guid\":{\"type\":\"string\"},\"kind\":{\"enum\":[\"scene\"],\"type\":\"string\"},\"path\":{\"pattern\":\"^(Assets|Packages)/\",\"type\":\"string\"}},\"required\":[\"kind\"],\"type\":[\"object\",\"string\"]}},\"required\":[\"kind\"],\"type\":\"object\"}},\"additionalProperties\":false,\"description\":\"Reference to a component on a GameObject: kind 'component', keys owner, type, index.\",\"properties\":{\"index\":{\"minimum\":0,\"type\":\"integer\"},\"kind\":{\"enum\":[\"component\"],\"type\":\"string\"},\"owner\":{\"$ref\":\"#/$defs/unity.GameObjectRef\"},\"type\":{\"pattern\":\"^(type:)?[A-Za-z][A-Za-z0-9_]*(\\\\.[A-Za-z][A-Za-z0-9_]*)*(?![\\\\s\\\\S])\",\"type\":\"string\"}},\"required\":[\"kind\"],\"type\":[\"object\",\"null\"]}"),
            };

        [Test]
        public void EveryVariantMatchesItsPinnedCanonicalBytes()
        {
            foreach (var pinned in s_pinnedVariantText)
            {
                Assert.AreEqual(
                    pinned.Value.Constrained,
                    Schema(pinned.Key, s_sweepConstraints).CanonicalText,
                    pinned.Key + " (constrained)");
                Assert.AreEqual(
                    pinned.Value.Nullable,
                    Schema(pinned.Key, nullable: true).CanonicalText,
                    pinned.Key + " (nullable)");
            }
        }

        [Test]
        public void RegeneratingFromAFreshRegistryYieldsIdenticalBytes()
        {
            // A second Registry built from the same providers is a second
            // generation of the whole snapshot: definition order, the $defs
            // collector and the canonical writer must contribute nothing
            // per-snapshot to the emitted bytes of any fixture variant.
            var fresh = TypeRegistryBuilder.Build(new[]
            {
                new TypeProviderCandidate(
                    CoreTypeProvider.ProviderId, new CoreTypeProvider(), OperationOrigin.Standard),
                new TypeProviderCandidate(
                    "opweave.unity-shapes", new UnityShapesProvider(), OperationOrigin.Standard),
            });
            Assert.AreEqual(0, fresh.Failures.Count);
            var variants = new (string Name, PortConstraints? Constraints, string? Description, bool Nullable)[]
            {
                ("plain", null, null, false),
                ("constrained", s_sweepConstraints, null, false),
                ("nullable", null, null, true),
                ("described", null, "Port doc.", false),
            };
            foreach (var typeId in s_allTypeUses)
            {
                foreach (var variant in variants)
                {
                    var again = TypeSchemas.Generate(
                        fresh.Registry, Type(typeId),
                        variant.Constraints, variant.Description, variant.Nullable);
                    Assert.IsTrue(again.Ok, typeId);
                    var first = Schema(
                        typeId, variant.Constraints, variant.Description, variant.Nullable);
                    Assert.AreEqual(
                        first.CanonicalText, again.Value!.CanonicalText,
                        typeId + " (" + variant.Name + ")");
                    Assert.AreEqual(
                        first.Sha256, again.Value!.Sha256,
                        typeId + " (" + variant.Name + ")");
                }
            }
        }

        // ── Errors (no new codes; the registry's own diagnostics) ───────────

        [Test]
        public void UnresolvableTypeReturnsTheRegistryDiagnostic()
        {
            var unknown = TypeSchemas.Generate(s_build.Registry, Type("acme.Missing"));
            Assert.IsFalse(unknown.Ok);
            Assert.AreEqual(ErrorCodes.OW_TYPE_001, unknown.Diagnostics[0].Code);

            var arity = TypeSchemas.Generate(s_build.Registry, Type("core.List"));
            Assert.IsFalse(arity.Ok);
            Assert.AreEqual(ErrorCodes.OW_TYPE_005, arity.Diagnostics[0].Code);
        }

        // ── Stubs for the external-Codec fallback and declared Schema ──────

        private static TypeRegistryBuildResult BuildAcmeRegistry()
        {
            var built = TypeRegistryBuilder.Build(new[]
            {
                new TypeProviderCandidate(
                    CoreTypeProvider.ProviderId, new CoreTypeProvider(), OperationOrigin.Standard),
                new TypeProviderCandidate(
                    "acme.widgets",
                    new StubProvider(),
                    OperationOrigin.Custom),
            });
            Assert.AreEqual(0, built.Failures.Count);
            return built;
        }

        private sealed class StubProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes()
            {
                yield return new TypeDefinition(
                    "acme.Widget",
                    TypeKind.Value,
                    0,
                    System.Array.Empty<string>(),
                    null,
                    "a test type",
                    new StubCodec())
                {
                    Schema = JsonValue.Parse(
                        @"{""type"":""string"",""pattern"":""^w[0-9]+(?![\\s\\S])""}").Value!,
                };
                yield return new TypeDefinition(
                    "acme.Box",
                    TypeKind.Value,
                    0,
                    System.Array.Empty<string>(),
                    null,
                    "a box",
                    new StubCodec())
                {
                    Schema = JsonValue.Parse(
                        @"{""additionalProperties"":false," +
                        @"""properties"":{""w"":{""$ref"":""#/$defs/acme.Widget""}}," +
                        @"""required"":[""w""],""type"":""object""}").Value!,
                };
                yield return new TypeDefinition(
                    "acme.Opaque",
                    TypeKind.Value,
                    0,
                    System.Array.Empty<string>(),
                    null,
                    "an opaque type",
                    new StubCodec());
                yield return new TypeDefinition(
                    "acme.Alias",
                    TypeKind.Value,
                    0,
                    System.Array.Empty<string>(),
                    null,
                    "an alias of Widget declared as a root $ref",
                    new StubCodec())
                {
                    Schema = JsonValue.Parse(
                        @"{""$ref"":""#/$defs/acme.Widget""}").Value!,
                };
                yield return new TypeDefinition(
                    "acme.Ranged",
                    TypeKind.Value,
                    0,
                    System.Array.Empty<string>(),
                    null,
                    "a bounded integer",
                    new StubCodec())
                {
                    Schema = JsonValue.Parse(
                        @"{""type"":""integer"",""minimum"":10,""maximum"":100}").Value!,
                };
                yield return new TypeDefinition(
                    "acme.Capped",
                    TypeKind.Value,
                    0,
                    System.Array.Empty<string>(),
                    null,
                    "a length-capped string",
                    new StubCodec())
                {
                    Schema = JsonValue.Parse(
                        @"{""type"":""string"",""maxLength"":4}").Value!,
                };
                yield return new TypeDefinition(
                    "acme.RootFormat",
                    TypeKind.Value,
                    0,
                    System.Array.Empty<string>(),
                    null,
                    "root keyword violation",
                    new StubCodec())
                {
                    Schema = JsonValue.Parse(
                        @"{""type"":""string"",""format"":""email""}").Value!,
                };
                yield return new TypeDefinition(
                    "acme.NestedFormat",
                    TypeKind.Value,
                    0,
                    System.Array.Empty<string>(),
                    null,
                    "nested keyword violation",
                    new StubCodec())
                {
                    Schema = JsonValue.Parse(
                        @"{""type"":""object""," +
                        @"""properties"":{""v"":{""type"":""string"",""format"":""email""}}}").Value!,
                };
                yield return new TypeDefinition(
                    "acme.DefsKeyword",
                    TypeKind.Value,
                    0,
                    System.Array.Empty<string>(),
                    null,
                    "defs keyword violation",
                    new StubCodec())
                {
                    Schema = JsonValue.Parse(
                        @"{""type"":""object""," +
                        @"""$defs"":{""helper"":{""minLength"":2}}}").Value!,
                };
                yield return new TypeDefinition(
                    "acme.ItemsKeyword",
                    TypeKind.Value,
                    0,
                    System.Array.Empty<string>(),
                    null,
                    "items keyword violation",
                    new StubCodec())
                {
                    Schema = JsonValue.Parse(
                        @"{""type"":""array"",""items"":{""oneOf"":[]}}").Value!,
                };
                yield return new TypeDefinition(
                    "acme.KeywordNamed",
                    TypeKind.Value,
                    0,
                    System.Array.Empty<string>(),
                    null,
                    "keyword-shaped member names",
                    new StubCodec())
                {
                    Schema = JsonValue.Parse(
                        @"{""type"":""object""," +
                        @"""properties"":{""format"":{""type"":""string""}}," +
                        @"""$defs"":{""oneOf"":{""type"":""integer""}}}").Value!,
                };
                yield return new TypeDefinition(
                    "acme.RefToInvalid",
                    TypeKind.Value,
                    0,
                    System.Array.Empty<string>(),
                    null,
                    "references a type whose schema is outside the subset",
                    new StubCodec())
                {
                    Schema = JsonValue.Parse(
                        @"{""type"":""object""," +
                        @"""properties"":{""v"":{""$ref"":""#/$defs/acme.NestedFormat""}}," +
                        @"""required"":[""v""]}").Value!,
                };
                yield return new TypeDefinition(
                    "acme.DeepShape",
                    TypeKind.Value,
                    0,
                    System.Array.Empty<string>(),
                    null,
                    "a fully in-subset nested shape",
                    new StubCodec())
                {
                    Schema = JsonValue.Parse(
                        @"{""type"":""object"",""additionalProperties"":false," +
                        @"""properties"":{" +
                        @"""xs"":{""type"":""array"",""items"":{""$ref"":""#/$defs/acme.Widget""}}," +
                        @"""extra"":{""type"":""object"",""additionalProperties"":{""type"":""string""}}}," +
                        @"""required"":[""xs""]," +
                        @"""$defs"":{""pair"":{""type"":""object""," +
                        @"""properties"":{""n"":{""type"":""integer""}}}}}").Value!,
                };
                yield return new TypeDefinition(
                    "acme.OddPattern",
                    TypeKind.Value,
                    0,
                    System.Array.Empty<string>(),
                    null,
                    "a declared pattern that is not a string",
                    new StubCodec())
                {
                    Schema = JsonValue.Parse(
                        @"{""pattern"":5}").Value!,
                };
            }
        }

        private sealed class LooseRefProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes()
            {
                yield return new TypeDefinition(
                    "acme.LooseBox",
                    TypeKind.Value,
                    0,
                    System.Array.Empty<string>(),
                    null,
                    "a box with a loosely spelled reference",
                    new StubCodec())
                {
                    Schema = JsonValue.Parse(
                        @"{""properties"":{""ns"":{""$ref"":""#/$defs/core.List< core.Int >""}}," +
                        @"""type"":""object""}").Value!,
                };
            }
        }

        private sealed class StubCodec : IValueCodec
        {
            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints) =>
                System.Array.Empty<Diagnostic>();

            public JsonValue Normalize(JsonValue value) => value;
        }
    }
}
