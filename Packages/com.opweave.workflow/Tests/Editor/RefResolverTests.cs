#nullable enable

// T-W2.3-01 [Edit] tests (local:T-W2.3-01:ref-resolution): the SPEC §G2
// RefResolver — resolution order for every Ref kind, the OW-UNITY-001..004
// error set, and ToRef filling every key the object can hold. GameObject
// fixtures live in the runner's active scene; tests that need a saved
// or untitled scene do the GetSceneManagerSetup save/restore dance (the
// same pattern as EditorRequirementEvaluatorTests) so they do not
// depend on the Editor's initial scene set or on test order.
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class RefResolverTests
    {
        private const string ProbeFolder = "Assets/OpWeaveRefProbe";
        private const string SingleProbePath = ProbeFolder + "/single.unity";
        private const string SceneProbePath = ProbeFolder + "/probe.unity";
        private const string PrefabProbePath = ProbeFolder + "/probe.prefab";
        private const string AssetProbePath = ProbeFolder + "/probe.asset";

        private RefResolver _resolver = null!;
        private readonly List<GameObject> _created = new List<GameObject>();
        private readonly List<Scene> _scenes = new List<Scene>();
        private SceneSetup[]? _savedSetup;

        [SetUp]
        public void SetUp()
        {
            _resolver = new RefResolver();
        }

        [TearDown]
        public void TearDown()
        {
            for (var i = 0; i < _created.Count; i++)
            {
                if (_created[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(_created[i]);
                }
            }
            _created.Clear();
            for (var i = 0; i < _scenes.Count; i++)
            {
                if (_scenes[i].IsValid() && _scenes[i].isLoaded)
                {
                    EditorSceneManager.CloseScene(_scenes[i], true);
                }
            }
            _scenes.Clear();
            if (_savedSetup is not null && _savedSetup.Length > 0)
            {
                EditorSceneManager.RestoreSceneManagerSetup(_savedSetup);
                _savedSetup = null;
            }
            if (AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.DeleteAsset(ProbeFolder);
            }
            AssetDatabase.Refresh();
        }

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, $"fixture JSON must parse: {text}");
            return parsed.Value!;
        }

        private static JsonValue RefOf(JObject obj) => JsonValue.From(obj);

        private GameObject Go(string name, GameObject? parent = null)
        {
            var go = new GameObject(name);
            if (parent is not null)
            {
                go.transform.SetParent(parent.transform, worldPositionStays: false);
            }
            _created.Add(go);
            return go;
        }

        // Opens a saved scene loaded additively. An unsaved untitled scene
        // blocks NewScene(Additive), so the current setup is saved, the
        // runner's scene is replaced by a saved single and the probe scene
        // is created and saved — all restored in TearDown.
        private Scene OpenSavedScene(string scenePath)
        {
            _savedSetup = EditorSceneManager.GetSceneManagerSetup();
            var single = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EnsureProbeFolder();
            Assert.IsTrue(
                EditorSceneManager.SaveScene(single, SingleProbePath),
                "the replacement single scene must save");
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            Assert.IsTrue(
                EditorSceneManager.SaveScene(scene, scenePath),
                "the probe scene must save");
            _scenes.Add(scene);
            return scene;
        }

        private static void EnsureProbeFolder()
        {
            if (!AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.CreateFolder("Assets", "OpWeaveRefProbe");
            }
        }

        private GameObject GoInScene(string name, Scene scene, GameObject? parent = null)
        {
            var go = new GameObject(name);
            SceneManager.MoveGameObjectToScene(go, scene);
            if (parent is not null)
            {
                go.transform.SetParent(parent.transform, worldPositionStays: false);
            }
            _created.Add(go);
            return go;
        }

        private GameObject CreatePrefab(out string guid)
        {
            EnsureProbeFolder();
            var source = Go("G2PrefabRoot");
            var prefab = PrefabUtility.SaveAsPrefabAsset(source, PrefabProbePath);
            Assert.IsNotNull(prefab, "fixture prefab must save");
            guid = AssetDatabase.AssetPathToGUID(PrefabProbePath);
            Assert.That(guid, Is.Not.Null.And.Not.EqualTo(""));
            return prefab;
        }

        private static string HandleOf(GameObject go) =>
            SessionHandle.Write(go).ToString();

        private static string CodeOf<T>(Result<T> result) =>
            result.Diagnostics.Count > 0 ? result.Diagnostics[0].Code : "<none>";

        // ── SPEC §G2: GameObjectRef resolution order ─────────────────────

        [Test]
        public void GameObjectRef_ResolveByHandle()
        {
            var go = Go("G2Target");
            var result = _resolver.Resolve<GameObject>(
                Json("{\"kind\":\"gameObject\",\"handle\":" + HandleOf(go) + "}"));
            Assert.IsTrue(result.Ok, $"resolve failed: {CodeOf(result)}");
            Assert.That(result.Value, Is.SameAs(go));
        }

        [Test]
        public void GameObjectRef_ResolveByHierarchyPathAcrossScenes()
        {
            var parent = Go("G2Parent");
            var child = Go("G2Child", parent);
            var result = _resolver.Resolve<GameObject>(
                Json("{\"kind\":\"gameObject\",\"path\":\"/G2Parent/G2Child\"}"));
            Assert.IsTrue(result.Ok, $"resolve failed: {CodeOf(result)}");
            Assert.That(result.Value, Is.SameAs(child));
        }

        [Test]
        public void GameObjectRef_ResolveByGlobalIdOnSavedScene()
        {
            var scene = OpenSavedScene(SceneProbePath);
            var go = GoInScene("G2Target", scene);
            var globalId = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString();
            Assert.IsTrue(GlobalObjectId.TryParse(globalId, out _),
                "a saved-scene object must have a stable globalId");
            var result = _resolver.Resolve<GameObject>(
                Json("{\"kind\":\"gameObject\",\"globalId\":\"" + globalId + "\"}"));
            Assert.IsTrue(result.Ok, $"resolve failed: {CodeOf(result)}");
            Assert.That(result.Value, Is.SameAs(go));
        }

        [Test]
        public void GameObjectRef_IndexedSiblingResolves()
        {
            var parent = Go("G2Parent");
            var first = Go("G2Dup", parent);
            var second = Go("G2Dup", parent);
            var result0 = _resolver.Resolve<GameObject>(
                Json("{\"kind\":\"gameObject\",\"path\":\"/G2Parent/G2Dup[0]\"}"));
            Assert.IsTrue(result0.Ok, $"[0] failed: {CodeOf(result0)}");
            Assert.That(result0.Value, Is.SameAs(first));
            var result1 = _resolver.Resolve<GameObject>(
                Json("{\"kind\":\"gameObject\",\"path\":\"/G2Parent/G2Dup[1]\"}"));
            Assert.IsTrue(result1.Ok, $"[1] failed: {CodeOf(result1)}");
            Assert.That(result1.Value, Is.SameAs(second));
        }

        [Test]
        public void GameObjectRef_SameNameSiblingsWithoutIndex_AreUnity002()
        {
            var parent = Go("G2Parent");
            Go("G2Dup", parent);
            Go("G2Dup", parent);
            var result = _resolver.Resolve<GameObject>(
                Json("{\"kind\":\"gameObject\",\"path\":\"/G2Parent/G2Dup\"}"));
            Assert.IsFalse(result.Ok);
            Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_UNITY_002));
        }

        [Test]
        public void GameObjectRef_UniqueRootIndexZero_Resolves()
        {
            var root = Go("G2UniqueRoot");
            // A "[0]" on a unique root name is the same target as the bare
            // name RefHierarchyPath.For emits — not an OW-UNITY-002.
            var result = _resolver.Resolve<GameObject>(
                Json("{\"kind\":\"gameObject\",\"path\":\"/G2UniqueRoot[0]\"}"));
            Assert.IsTrue(result.Ok, $"resolve failed: {CodeOf(result)}");
            Assert.That(result.Value, Is.SameAs(root));
        }

        [Test]
        public void GameObjectRef_SameNameRoots_KeepIndexRules()
        {
            Go("G2DupRoot");
            var second = Go("G2DupRoot");
            // A bare name stays ambiguous (OW-UNITY-002); an in-range
            // index still selects that same-name sibling.
            var bare = _resolver.Resolve<GameObject>(
                Json("{\"kind\":\"gameObject\",\"path\":\"/G2DupRoot\"}"));
            Assert.IsFalse(bare.Ok);
            Assert.That(CodeOf(bare), Is.EqualTo(ErrorCodes.OW_UNITY_002));
            var indexed = _resolver.Resolve<GameObject>(
                Json("{\"kind\":\"gameObject\",\"path\":\"/G2DupRoot[1]\"}"));
            Assert.IsTrue(indexed.Ok, $"[1] failed: {CodeOf(indexed)}");
            Assert.That(indexed.Value, Is.SameAs(second));
        }

        [Test]
        public void GameObjectRef_HandlePlusIndexedPath_Resolves()
        {
            var root = Go("G2UniqueRoot");
            // The handle resolves; the "[0]" path claim names the same
            // object and must not read as a key conflict (OW-UNITY-002).
            var result = _resolver.Resolve<GameObject>(RefOf(new JObject
            {
                ["kind"] = "gameObject",
                ["handle"] = SessionHandle.Write(root),
                ["path"] = "/G2UniqueRoot[0]",
            }));
            Assert.IsTrue(result.Ok, $"resolve failed: {CodeOf(result)}");
            Assert.That(result.Value, Is.SameAs(root));
        }

        [Test]
        public void GameObjectRef_EscapedSlashResolvesAndRoundTrips()
        {
            var parent = Go("G2Parent");
            var child = Go("G2A/B", parent);
            // The JSON text "G2A\\/B" parses to the HierarchyPath value
            // 'G2A\/B' — the §G2 escaped-slash name character.
            var result = _resolver.Resolve<GameObject>(
                Json("{\"kind\":\"gameObject\",\"path\":\"/G2Parent/G2A\\\\/B\"}"));
            Assert.IsTrue(result.Ok, $"resolve failed: {CodeOf(result)}");
            Assert.That(result.Value, Is.SameAs(child));

            var written = _resolver.ToRef(child, "unity.GameObjectRef").ToToken();
            Assert.That((string?)written["path"], Is.EqualTo("/G2Parent/G2A\\/B"));
        }

        [Test]
        public void GameObjectRef_MissingTarget_IsUnity001()
        {
            var result = _resolver.Resolve<GameObject>(
                Json("{\"kind\":\"gameObject\",\"path\":\"/G2Parent/G2Gone\"}"));
            Assert.IsFalse(result.Ok);
            Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_UNITY_001));
        }

        [Test]
        public void GameObjectRef_IndexOutOfRange_IsUnity001()
        {
            var parent = Go("G2Parent");
            Go("G2Dup", parent);
            var result = _resolver.Resolve<GameObject>(
                Json("{\"kind\":\"gameObject\",\"path\":\"/G2Parent/G2Dup[1]\"}"));
            Assert.IsFalse(result.Ok);
            Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_UNITY_001));
        }

        [Test]
        public void GameObjectRef_ConflictingKeys_AreUnity002()
        {
            var parent = Go("G2Parent");
            var child = Go("G2Child", parent);
            var other = Go("G2Other");
            // The handle names 'other'; the path names 'child' — the keys
            // contradict (SPEC §G2: resolved entity vs other keys).
            var result = _resolver.Resolve<GameObject>(RefOf(new JObject
            {
                ["kind"] = "gameObject",
                ["handle"] = SessionHandle.Write(other),
                ["path"] = "/G2Parent/G2Child",
            }));
            Assert.IsFalse(result.Ok);
            Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_UNITY_002));
        }

        [Test]
        public void GameObjectRef_StaleHandleConflictsWithResolvedKey_IsUnity002()
        {
            var parent = Go("G2Parent");
            var child = Go("G2Child", parent);
            var stale = Go("G2Stale");
            var staleHandle = SessionHandle.Write(stale);
            UnityEngine.Object.DestroyImmediate(stale);
            // The handle resolves to nothing; the path resolves 'child' —
            // the claimed handle still contradicts the resolved entity.
            var result = _resolver.Resolve<GameObject>(RefOf(new JObject
            {
                ["kind"] = "gameObject",
                ["handle"] = staleHandle,
                ["path"] = "/G2Parent/G2Child",
            }));
            Assert.IsFalse(result.Ok);
            Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_UNITY_002));
        }

        [Test]
        public void GameObjectRef_UnloadedScene_IsUnity003()
        {
            var scene = OpenSavedScene(SceneProbePath);
            Assert.IsTrue(EditorSceneManager.CloseScene(scene, true),
                "the probe scene must close");
            _scenes.Remove(scene);
            var result = _resolver.Resolve<GameObject>(RefOf(new JObject
            {
                ["kind"] = "gameObject",
                ["scene"] = new JObject { ["kind"] = "scene", ["path"] = SceneProbePath },
                ["path"] = "/G2Target",
            }));
            Assert.IsFalse(result.Ok);
            Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_UNITY_003));
        }

        [Test]
        public void GameObjectRef_PrefabAsset_IsUnity004()
        {
            var prefab = CreatePrefab(out _);
            // A GameObjectRef whose handle names the persistent prefab
            // asset is the SPEC §G2 asset/instance mix-up.
            var result = _resolver.Resolve<GameObject>(RefOf(new JObject
            {
                ["kind"] = "gameObject",
                ["handle"] = SessionHandle.Write(prefab),
            }));
            Assert.IsFalse(result.Ok);
            Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_UNITY_004));
        }

        [Test]
        public void GameObjectRef_PrefabAssetGlobalId_IsUnity004()
        {
            var prefab = CreatePrefab(out _);
            var globalId = GlobalObjectId.GetGlobalObjectIdSlow(prefab).ToString();
            var result = _resolver.Resolve<GameObject>(RefOf(new JObject
            {
                ["kind"] = "gameObject",
                ["globalId"] = globalId,
            }));
            Assert.IsFalse(result.Ok);
            Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_UNITY_004));
        }

        [Test]
        public void GameObjectRef_ResolvedInWrongScene_IsUnity002()
        {
            var scene = OpenSavedScene(SceneProbePath);
            var go = GoInScene("G2Target", scene);
            var result = _resolver.Resolve<GameObject>(RefOf(new JObject
            {
                ["kind"] = "gameObject",
                ["handle"] = SessionHandle.Write(go),
                ["scene"] = new JObject
                {
                    ["kind"] = "scene",
                    ["path"] = "Assets/Elsewhere.unity",
                },
            }));
            Assert.IsFalse(result.Ok);
            Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_UNITY_002));
        }

        [Test]
        public void GameObjectRef_WrongKindForT_IsUnity002()
        {
            var go = Go("G2Target");
            var material = _resolver.Resolve<Material>(
                Json("{\"kind\":\"gameObject\",\"handle\":" + HandleOf(go) + "}"));
            Assert.IsFalse(material.Ok);
            Assert.That(CodeOf(material), Is.EqualTo(ErrorCodes.OW_UNITY_002));

            var scene = _resolver.Resolve<UnityEngine.Object>(
                Json("{\"kind\":\"scene\",\"path\":\"Assets/x.unity\"}"));
            Assert.IsFalse(scene.Ok);
            Assert.That(CodeOf(scene), Is.EqualTo(ErrorCodes.OW_UNITY_002));
        }

        [Test]
        public void GameObjectRef_MalformedRef_IsType003()
        {
            var result = _resolver.Resolve<GameObject>(Json("{}"));
            Assert.IsFalse(result.Ok);
            Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_TYPE_003));
        }

        // ── SPEC §G2: PrefabInstanceRef ──────────────────────────────────

        [Test]
        public void PrefabInstanceRef_ResolvesOutermostRoot()
        {
            var prefab = CreatePrefab(out var guid);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab)!;
            _created.Add(instance);
            var result = _resolver.Resolve<GameObject>(RefOf(new JObject
            {
                ["kind"] = "prefabInstance",
                ["handle"] = SessionHandle.Write(instance),
                ["prefabAsset"] = guid,
            }));
            Assert.IsTrue(result.Ok, $"resolve failed: {CodeOf(result)}");
            Assert.That(result.Value, Is.SameAs(instance));
        }

        [Test]
        public void PrefabInstanceRef_NonRoot_IsUnity004()
        {
            var prefab = CreatePrefab(out _);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab)!;
            _created.Add(instance);
            var child = Go("G2PlainChild", instance);
            var result = _resolver.Resolve<GameObject>(RefOf(new JObject
            {
                ["kind"] = "prefabInstance",
                ["handle"] = SessionHandle.Write(child),
            }));
            Assert.IsFalse(result.Ok);
            Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_UNITY_004));
        }

        [Test]
        public void PrefabInstanceRef_WrongPrefabAssetGuid_IsUnity002()
        {
            var prefab = CreatePrefab(out _);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab)!;
            _created.Add(instance);
            var result = _resolver.Resolve<GameObject>(RefOf(new JObject
            {
                ["kind"] = "prefabInstance",
                ["handle"] = SessionHandle.Write(instance),
                ["prefabAsset"] = "00000000000000000000000000000000",
            }));
            Assert.IsFalse(result.Ok);
            Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_UNITY_002));
        }

        // ── SPEC §G2: prefab-contents scene through Run resources ───────

        [Test]
        public void PrefabContents_ResolvesThroughRunResources()
        {
            CreatePrefab(out var guid);
            var contents = PrefabUtility.LoadPrefabContents(PrefabProbePath);
            try
            {
                var child = new GameObject("G2ContentsChild");
                child.transform.SetParent(contents.transform, worldPositionStays: false);
                var resolver = new RefResolver(
                    g => g == guid ? contents : null);
                var result = resolver.Resolve<GameObject>(RefOf(new JObject
                {
                    ["kind"] = "gameObject",
                    ["scene"] = "prefab-contents:" + guid,
                    ["path"] = "/" + contents.name + "/G2ContentsChild",
                }));
                Assert.IsTrue(result.Ok, $"resolve failed: {CodeOf(result)}");
                Assert.That(result.Value!.name, Is.EqualTo("G2ContentsChild"));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        [Test]
        public void PrefabContents_WithoutRunResource_IsUnity003()
        {
            var result = _resolver.Resolve<GameObject>(RefOf(new JObject
            {
                ["kind"] = "gameObject",
                ["scene"] = "prefab-contents:0123456789abcdef0123456789abcdef",
                ["path"] = "/Root",
            }));
            Assert.IsFalse(result.Ok);
            Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_UNITY_003));
        }

        [Test]
        public void PrefabContents_HandleWithoutRunResource_IsUnity003()
        {
            CreatePrefab(out var guid);
            var contents = PrefabUtility.LoadPrefabContents(PrefabProbePath);
            try
            {
                // A live handle into the contents does not bypass the
                // scene key: prefab-contents refs resolve through the
                // Run resources, and none is registered here (§G2).
                var result = _resolver.Resolve<GameObject>(RefOf(new JObject
                {
                    ["kind"] = "gameObject",
                    ["handle"] = SessionHandle.Write(contents),
                    ["scene"] = "prefab-contents:" + guid,
                }));
                Assert.IsFalse(result.Ok);
                Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_UNITY_003));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        [Test]
        public void PrefabContents_HandleIntoOtherContents_IsUnity002()
        {
            CreatePrefab(out _);
            var contents = PrefabUtility.LoadPrefabContents(PrefabProbePath);
            var otherSource = Go("G2PrefabRootB");
            var otherPath = ProbeFolder + "/other.prefab";
            Assert.IsNotNull(
                PrefabUtility.SaveAsPrefabAsset(otherSource, otherPath),
                "the second fixture prefab must save");
            var otherGuid = AssetDatabase.AssetPathToGUID(otherPath);
            var otherContents = PrefabUtility.LoadPrefabContents(otherPath);
            try
            {
                var resolver = new RefResolver(
                    g => g == otherGuid ? otherContents : null);
                // The handle names an object in the first prefab's
                // contents; the scene key names the second prefab's
                // registered contents — the resolved object does not
                // belong to that resource.
                var result = resolver.Resolve<GameObject>(RefOf(new JObject
                {
                    ["kind"] = "gameObject",
                    ["handle"] = SessionHandle.Write(contents),
                    ["scene"] = "prefab-contents:" + otherGuid,
                }));
                Assert.IsFalse(result.Ok);
                Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_UNITY_002));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(otherContents);
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        [Test]
        public void PrefabContents_HandleWithMatchingResource_Resolves()
        {
            CreatePrefab(out var guid);
            var contents = PrefabUtility.LoadPrefabContents(PrefabProbePath);
            try
            {
                var child = new GameObject("G2ContentsChild");
                child.transform.SetParent(
                    contents.transform, worldPositionStays: false);
                var resolver = new RefResolver(
                    g => g == guid ? contents : null);
                // The higher-priority handle key resolves the contents
                // child; the prefab-contents scene key is verified
                // against the registered Run resource's contents root.
                var result = resolver.Resolve<GameObject>(RefOf(new JObject
                {
                    ["kind"] = "gameObject",
                    ["handle"] = SessionHandle.Write(child),
                    ["scene"] = "prefab-contents:" + guid,
                }));
                Assert.IsTrue(result.Ok, $"resolve failed: {CodeOf(result)}");
                Assert.That(result.Value, Is.SameAs(child));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        [Test]
        public void PrefabContents_ToRefWritesPrefabContentsScene()
        {
            CreatePrefab(out var guid);
            var contents = PrefabUtility.LoadPrefabContents(PrefabProbePath);
            try
            {
                var written = _resolver.ToRef(contents, "unity.GameObjectRef").ToToken();
                Assert.That(
                    (string?)written["scene"],
                    Is.EqualTo("prefab-contents:" + guid));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        // ── SPEC §G2: ComponentRef ───────────────────────────────────────

        [Test]
        public void ComponentRef_ResolvesByOwnerTypeAndIndex()
        {
            var go = Go("G2Owner");
            var animator = go.AddComponent<Animator>();
            var result = _resolver.Resolve<Animator>(RefOf(new JObject
            {
                ["kind"] = "component",
                ["owner"] = new JObject
                {
                    ["kind"] = "gameObject",
                    ["handle"] = SessionHandle.Write(go),
                },
                ["type"] = "unity.Animator",
                ["index"] = 0,
            }));
            Assert.IsTrue(result.Ok, $"resolve failed: {CodeOf(result)}");
            Assert.That(result.Value, Is.SameAs(animator));
        }

        [Test]
        public void ComponentRef_TypePrefixAndIndexSelect()
        {
            var go = Go("G2Owner");
            go.AddComponent<BoxCollider>();
            var collider = go.AddComponent<BoxCollider>();
            var result = _resolver.Resolve<Component>(RefOf(new JObject
            {
                ["kind"] = "component",
                ["owner"] = new JObject
                {
                    ["kind"] = "gameObject",
                    ["handle"] = SessionHandle.Write(go),
                },
                ["type"] = "type:UnityEngine.BoxCollider",
                ["index"] = 1,
            }));
            Assert.IsTrue(result.Ok, $"resolve failed: {CodeOf(result)}");
            Assert.That(result.Value, Is.SameAs(collider));
        }

        [Test]
        public void ComponentRef_UnknownType_IsUnity001()
        {
            var go = Go("G2Owner");
            var result = _resolver.Resolve<Component>(RefOf(new JObject
            {
                ["kind"] = "component",
                ["owner"] = new JObject
                {
                    ["kind"] = "gameObject",
                    ["handle"] = SessionHandle.Write(go),
                },
                ["type"] = "type:OpWeave.Missing.Component",
            }));
            Assert.IsFalse(result.Ok);
            Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_UNITY_001));
        }

        [Test]
        public void ComponentRef_IndexOutOfRange_IsUnity001()
        {
            var go = Go("G2Owner");
            go.AddComponent<Animator>();
            var result = _resolver.Resolve<Component>(RefOf(new JObject
            {
                ["kind"] = "component",
                ["owner"] = new JObject
                {
                    ["kind"] = "gameObject",
                    ["handle"] = SessionHandle.Write(go),
                },
                ["index"] = 7,
            }));
            Assert.IsFalse(result.Ok);
            Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_UNITY_001));
        }

        [Test]
        public void ComponentRef_GameObjectT_IsUnity002()
        {
            var go = Go("G2Owner");
            var result = _resolver.Resolve<GameObject>(RefOf(new JObject
            {
                ["kind"] = "component",
                ["owner"] = new JObject
                {
                    ["kind"] = "gameObject",
                    ["handle"] = SessionHandle.Write(go),
                },
                ["index"] = 0,
            }));
            Assert.IsFalse(result.Ok);
            Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_UNITY_002));
        }

        // ── SPEC §G2: AssetRef / PrefabAssetRef ──────────────────────────

        private sealed class ProbeSubAsset : ScriptableObject
        {
        }

        [Test]
        public void AssetRef_ResolvesByGuid()
        {
            EnsureProbeFolder();
            AssetDatabase.CreateAsset(
                new Material(Shader.Find("Hidden/InternalErrorShader")), AssetProbePath);
            var asset = AssetDatabase.LoadAssetAtPath<Material>(AssetProbePath);
            var guid = AssetDatabase.AssetPathToGUID(AssetProbePath);
            var result = _resolver.Resolve<Material>(RefOf(new JObject
            {
                ["kind"] = "asset",
                ["guid"] = guid,
            }));
            Assert.IsTrue(result.Ok, $"resolve failed: {CodeOf(result)}");
            Assert.That(result.Value, Is.SameAs(asset));
        }

        [Test]
        public void AssetRef_GuidPathConflict_IsUnity002()
        {
            EnsureProbeFolder();
            AssetDatabase.CreateAsset(
                new Material(Shader.Find("Hidden/InternalErrorShader")), AssetProbePath);
            var result = _resolver.Resolve<UnityEngine.Object>(RefOf(new JObject
            {
                ["kind"] = "asset",
                ["guid"] = "00000000000000000000000000000000",
                ["path"] = AssetProbePath,
            }));
            Assert.IsFalse(result.Ok);
            Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_UNITY_002));
        }

        [Test]
        public void AssetRef_Missing_IsUnity001()
        {
            EnsureProbeFolder();
            var result = _resolver.Resolve<UnityEngine.Object>(
                Json("{\"kind\":\"asset\",\"path\":\"Assets/OpWeaveRefProbe/gone.asset\"}"));
            Assert.IsFalse(result.Ok);
            Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_UNITY_001));
        }

        [Test]
        public void AssetRef_LocalIdSelectsSubAsset()
        {
            EnsureProbeFolder();
            var main = ScriptableObject.CreateInstance<ProbeSubAsset>();
            AssetDatabase.CreateAsset(main, AssetProbePath);
            var sub = new Material(Shader.Find("Hidden/InternalErrorShader"));
            AssetDatabase.AddObjectToAsset(sub, AssetProbePath);
            AssetDatabase.SaveAssets();
            Assert.IsTrue(
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    sub, out _, out var localId),
                "the sub-asset must have a local file id");
            var result = _resolver.Resolve<Material>(RefOf(new JObject
            {
                ["kind"] = "asset",
                ["path"] = AssetProbePath,
                ["localId"] = localId,
            }));
            Assert.IsTrue(result.Ok, $"resolve failed: {CodeOf(result)}");
            // The persisted sub-asset is what the localId names; a freshly
            // loaded instance is not guaranteed to be the same managed
            // object as the one AddObjectToAsset was given.
            Assert.IsTrue(
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    result.Value!, out _, out var resolvedLocalId),
                "the resolved sub-asset must carry a local file id");
            Assert.That(resolvedLocalId, Is.EqualTo(localId));
        }

        [Test]
        public void AssetRef_RequestedTypeBelowMainAsset_Resolves()
        {
            EnsureProbeFolder();
            var main = ScriptableObject.CreateInstance<ProbeSubAsset>();
            AssetDatabase.CreateAsset(main, AssetProbePath);
            var sub = new Material(Shader.Find("Hidden/InternalErrorShader"));
            AssetDatabase.AddObjectToAsset(sub, AssetProbePath);
            AssetDatabase.SaveAssets();
            Assert.IsTrue(
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    sub, out _, out var subLocalId),
                "the sub-asset must have a local file id");
            // SPEC §G2: LoadAssetAtPath<T> selects the first object of
            // the requested type at the path — a Material sub-asset
            // under a ScriptableObject main asset resolves.
            var result = _resolver.Resolve<Material>(RefOf(new JObject
            {
                ["kind"] = "asset",
                ["path"] = AssetProbePath,
            }));
            Assert.IsTrue(result.Ok, $"resolve failed: {CodeOf(result)}");
            Assert.IsTrue(
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    result.Value!, out _, out var resolvedLocalId),
                "the resolved asset must carry a local file id");
            Assert.That(resolvedLocalId, Is.EqualTo(subLocalId));
        }

        [Test]
        public void AssetRef_UnknownLocalId_IsUnity001()
        {
            EnsureProbeFolder();
            AssetDatabase.CreateAsset(
                new Material(Shader.Find("Hidden/InternalErrorShader")), AssetProbePath);
            var result = _resolver.Resolve<UnityEngine.Object>(RefOf(new JObject
            {
                ["kind"] = "asset",
                ["path"] = AssetProbePath,
                ["localId"] = 42424242,
            }));
            Assert.IsFalse(result.Ok);
            Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_UNITY_001));
        }

        [Test]
        public void AssetRef_GameObjectT_ResolvesPrefabRoot()
        {
            var prefab = CreatePrefab(out var guid);
            // SPEC §B5 allows AssetRef<T> for Unity class types including
            // unity.GameObject: an 'asset' kind ref resolves the prefab's
            // root GameObject through the asset table row (§G2), not
            // OW-UNITY-004.
            var result = _resolver.Resolve<GameObject>(RefOf(new JObject
            {
                ["kind"] = "asset",
                ["guid"] = guid,
            }));
            Assert.IsTrue(result.Ok, $"resolve failed: {CodeOf(result)}");
            Assert.That(result.Value, Is.SameAs(prefab));
        }

        [Test]
        public void AssetRef_ComponentT_ResolvesPrefabRootComponent()
        {
            var prefab = CreatePrefab(out var guid);
            // A Component T is the same §B5/§G2 case: LoadAssetAtPath<T>
            // resolves the component the prefab asset's root carries.
            var result = _resolver.Resolve<Transform>(RefOf(new JObject
            {
                ["kind"] = "asset",
                ["guid"] = guid,
            }));
            Assert.IsTrue(result.Ok, $"resolve failed: {CodeOf(result)}");
            Assert.That(result.Value, Is.SameAs(prefab.transform));
        }

        [Test]
        public void PrefabAssetRef_ResolvesPrefab()
        {
            var prefab = CreatePrefab(out var guid);
            var result = _resolver.Resolve<UnityEngine.Object>(RefOf(new JObject
            {
                ["kind"] = "prefabAsset",
                ["guid"] = guid,
            }));
            Assert.IsTrue(result.Ok, $"resolve failed: {CodeOf(result)}");
            Assert.That(result.Value, Is.SameAs(prefab));
        }

        [Test]
        public void PrefabAssetRef_GameObjectT_ResolvesPrefabRoot()
        {
            var prefab = CreatePrefab(out var guid);
            // SPEC §B5: PrefabAssetRef is AssetRef<unity.GameObject> —
            // a GameObject T resolves the prefab asset's root.
            var result = _resolver.Resolve<GameObject>(RefOf(new JObject
            {
                ["kind"] = "prefabAsset",
                ["guid"] = guid,
            }));
            Assert.IsTrue(result.Ok, $"resolve failed: {CodeOf(result)}");
            Assert.That(result.Value, Is.SameAs(prefab));
        }

        [Test]
        public void PrefabAssetRef_NonPrefab_IsUnity004()
        {
            EnsureProbeFolder();
            AssetDatabase.CreateAsset(
                new Material(Shader.Find("Hidden/InternalErrorShader")), AssetProbePath);
            var guid = AssetDatabase.AssetPathToGUID(AssetProbePath);
            var result = _resolver.Resolve<UnityEngine.Object>(RefOf(new JObject
            {
                ["kind"] = "prefabAsset",
                ["guid"] = guid,
            }));
            Assert.IsFalse(result.Ok);
            Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_UNITY_004));
        }

        // ── SPEC §G2: SceneRef resolution ────────────────────────────────

        [Test]
        public void SceneRef_ResolvesLoadedSceneByPathAndGuid()
        {
            var scene = OpenSavedScene(SceneProbePath);
            var guid = AssetDatabase.AssetPathToGUID(SceneProbePath);

            var byPath = _resolver.ResolveScene(
                Json("{\"kind\":\"scene\",\"path\":\"" + SceneProbePath + "\"}"));
            Assert.IsTrue(byPath.Ok, $"by path failed: {CodeOf(byPath)}");
            Assert.That(byPath.Value!.path, Is.EqualTo(SceneProbePath));

            var byGuid = _resolver.ResolveScene(
                Json("{\"kind\":\"scene\",\"guid\":\"" + guid + "\"}"));
            Assert.IsTrue(byGuid.Ok, $"by guid failed: {CodeOf(byGuid)}");
            Assert.That(byGuid.Value!.path, Is.EqualTo(SceneProbePath));
        }

        [Test]
        public void SceneRef_Unloaded_IsUnity003()
        {
            EnsureProbeFolder();
            var scene = OpenSavedScene(SceneProbePath);
            Assert.IsTrue(EditorSceneManager.CloseScene(scene, true));
            _scenes.Remove(scene);
            var result = _resolver.ResolveScene(
                Json("{\"kind\":\"scene\",\"path\":\"" + SceneProbePath + "\"}"));
            Assert.IsFalse(result.Ok);
            Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_UNITY_003));
        }

        // ── SPEC §G2: ToRef fills every key the object can hold ─────────

        [Test]
        public void ToRef_SavedSceneGameObject_FillsAllKeys()
        {
            var scene = OpenSavedScene(SceneProbePath);
            var go = GoInScene("G2Target", scene);
            var written = (JObject)_resolver.ToRef(go, "unity.GameObjectRef").ToToken();
            Assert.That((string?)written["kind"], Is.EqualTo("gameObject"));
            var sceneRef = written["scene"] as JObject;
            Assert.IsNotNull(sceneRef);
            Assert.That((string?)sceneRef!["path"], Is.EqualTo(SceneProbePath));
            Assert.That(
                (string?)sceneRef["guid"],
                Is.EqualTo(AssetDatabase.AssetPathToGUID(SceneProbePath)));
            Assert.That((string?)written["path"], Is.EqualTo("/G2Target"));
            Assert.IsNotNull(written["globalId"]);
            Assert.IsNotNull(written["handle"]);

            // The fully keyed ref resolves back to the same object.
            var back = _resolver.Resolve<GameObject>(JsonValue.From(written));
            Assert.IsTrue(back.Ok, $"round-trip failed: {CodeOf(back)}");
            Assert.That(back.Value, Is.SameAs(go));
        }

        [Test]
        public void ToRef_UnsavedSceneGameObject_UsesHandleAndPathOnly()
        {
            // The untitled active scene must not be inherited from the
            // Editor's initial scene set or from test order: save the
            // current setup, then replace it with a fresh untitled scene
            // (NewSceneMode.Single is allowed even when the current scene
            // is saved). TearDown restores the saved setup, discarding
            // the untitled scene without a save prompt.
            _savedSetup = EditorSceneManager.GetSceneManagerSetup();
            EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var go = Go("G2Target");
            Assert.That(go.scene.path, Is.EqualTo(""),
                "the fixture must live in an unsaved (untitled) scene");
            var written = (JObject)_resolver.ToRef(go, "unity.GameObjectRef").ToToken();
            Assert.That((string?)written["kind"], Is.EqualTo("gameObject"));
            Assert.IsNull(written["scene"], "untitled scene emits no scene key");
            Assert.IsNull(written["globalId"], "untitled scene emits no globalId");
            Assert.That((string?)written["path"], Is.EqualTo("/G2Target"));
            Assert.IsNotNull(written["handle"]);
        }

        [Test]
        public void ToRef_PrefabInstance_FillsPrefabAssetKey()
        {
            var prefab = CreatePrefab(out var guid);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab)!;
            _created.Add(instance);
            var written = _resolver.ToRef(instance, "unity.PrefabInstanceRef").ToToken();
            Assert.That((string?)written["kind"], Is.EqualTo("prefabInstance"));
            Assert.That((string?)written["prefabAsset"], Is.EqualTo(guid));
        }

        [Test]
        public void ToRef_Component_FillsOwnerTypeAndIndex()
        {
            var go = Go("G2Owner");
            var animator = go.AddComponent<Animator>();
            var written = _resolver
                .ToRef(animator, "unity.ComponentRef<unity.Animator>").ToToken();
            Assert.That((string?)written["kind"], Is.EqualTo("component"));
            Assert.That((string?)written["type"], Is.EqualTo("unity.Animator"));
            Assert.That((long?)written["index"], Is.EqualTo(0L));
            var owner = written["owner"] as JObject;
            Assert.IsNotNull(owner);
            Assert.That((string?)owner!["kind"], Is.EqualTo("gameObject"));
        }

        [Test]
        public void ToRef_Asset_FillsGuidAndPath()
        {
            EnsureProbeFolder();
            var asset = new Material(Shader.Find("Hidden/InternalErrorShader"));
            AssetDatabase.CreateAsset(asset, AssetProbePath);
            var written = _resolver
                .ToRef(asset, "unity.AssetRef<unity.Material>").ToToken();
            Assert.That((string?)written["kind"], Is.EqualTo("asset"));
            Assert.That(
                (string?)written["guid"],
                Is.EqualTo(AssetDatabase.AssetPathToGUID(AssetProbePath)));
            Assert.That((string?)written["path"], Is.EqualTo(AssetProbePath));
        }

        [Test]
        public void ToRef_SubAsset_FillsLocalId()
        {
            EnsureProbeFolder();
            var main = ScriptableObject.CreateInstance<ProbeSubAsset>();
            AssetDatabase.CreateAsset(main, AssetProbePath);
            var shader = Shader.Find("Hidden/InternalErrorShader");
            Assert.IsNotNull(shader, "fixture shader must exist");
            var sub = new Material(shader!);
            AssetDatabase.AddObjectToAsset(sub, AssetProbePath);
            AssetDatabase.SaveAssets();
            var written = _resolver
                .ToRef(sub, "unity.AssetRef<unity.Material>").ToToken();
            Assert.That(written["localId"], Is.Not.Null);
        }

        [Test]
        public void ToRef_PrefabAsset_FillsPrefabAssetKind()
        {
            var prefab = CreatePrefab(out var guid);
            var written = _resolver.ToRef(prefab, "unity.PrefabAssetRef").ToToken();
            Assert.That((string?)written["kind"], Is.EqualTo("prefabAsset"));
            Assert.That((string?)written["guid"], Is.EqualTo(guid));
            Assert.That((string?)written["path"], Is.EqualTo(PrefabProbePath));
        }

        [Test]
        public void ToRef_AssetRefOfPrefabRoot_EmitsAssetKind()
        {
            var prefab = CreatePrefab(out var guid);
            // SPEC §B5: the kind is the Ref Type ID's discriminator, not the
            // object's CLR type — unity.AssetRef<unity.GameObject> is an
            // 'asset' kind even though the object is a GameObject.
            var written = _resolver.ToRef(prefab, "unity.AssetRef<unity.GameObject>");
            var token = (JObject)written.ToToken();
            Assert.That((string?)token["kind"], Is.EqualTo("asset"));
            Assert.That((string?)token["guid"], Is.EqualTo(guid));
            Assert.That((string?)token["path"], Is.EqualTo(PrefabProbePath));
            Assert.IsNull(token["localId"], "the main asset carries no localId");
            Assert.That(
                UnityRefCodec.AssetRef.Validate(written, null), Is.Empty,
                "the emitted ref must pass unity.AssetRef<T> codec validation");
            var back = _resolver.Resolve<GameObject>(written);
            Assert.IsTrue(back.Ok, $"round-trip failed: {CodeOf(back)}");
            Assert.That(back.Value, Is.SameAs(prefab));
        }

        [Test]
        public void ToRef_AssetRefOfPrefabChild_EmitsAssetKindWithLocalId()
        {
            var source = Go("G2PrefabRoot");
            Go("G2PrefabChild", source);
            EnsureProbeFolder();
            var prefab = PrefabUtility.SaveAsPrefabAsset(source, PrefabProbePath);
            Assert.IsNotNull(prefab, "fixture prefab must save");
            var child = prefab.transform.GetChild(0).gameObject;
            Assert.IsFalse(AssetDatabase.IsMainAsset(child),
                "the fixture child must be a prefab sub-asset");
            var written = _resolver.ToRef(child, "unity.AssetRef<unity.GameObject>");
            var token = (JObject)written.ToToken();
            Assert.That((string?)token["kind"], Is.EqualTo("asset"));
            Assert.IsNotNull(token["localId"],
                "a non-main GameObject asset carries a localId — only an " +
                "'asset' kind admits that key");
            Assert.That(
                UnityRefCodec.AssetRef.Validate(written, null), Is.Empty,
                "the emitted ref must pass unity.AssetRef<T> codec validation");
            var back = _resolver.Resolve<GameObject>(written);
            Assert.IsTrue(back.Ok, $"round-trip failed: {CodeOf(back)}");
            Assert.That(back.Value, Is.SameAs(child));
        }

        [Test]
        public void ToRef_IncompatiblePair_Throws()
        {
            var go = Go("G2Target");
            Assert.Throws<ArgumentException>(
                () => _resolver.ToRef(go, "unity.PrefabAssetRef"));
            Assert.Throws<ArgumentException>(
                () => _resolver.ToRef(go, "unity.PrefabInstanceRef"));
        }

        // ── T-W2.3-07 / Issue #300: Editor fake-null ─────────────────────
        // A destroyed-but-referenced UnityEngine.Object is Editor
        // fake-null: `is null` misses it where `== null` catches it, so a
        // null-pattern check treats the dead object as live. Every site the
        // resolver or the evidence helpers intend as "missing" must read a
        // destroyed object as absent.

        [Test]
        public void GameObjectRef_DestroyedHandleTarget_IsUnity001()
        {
            var go = Go("G2Doomed");
            var handle = SessionHandle.Write(go);
            UnityEngine.Object.DestroyImmediate(go);
            var result = _resolver.Resolve<GameObject>(RefOf(new JObject
            {
                ["kind"] = "gameObject",
                ["handle"] = handle,
            }));
            Assert.IsFalse(result.Ok);
            Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_UNITY_001));
        }

        [Test]
        public void GameObjectRef_DestroyedGlobalIdTarget_IsUnity001()
        {
            var scene = OpenSavedScene(SceneProbePath);
            var go = GoInScene("G2Doomed", scene);
            var globalId = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString();
            Assert.IsTrue(GlobalObjectId.TryParse(globalId, out _),
                "a saved-scene object must have a stable globalId");
            UnityEngine.Object.DestroyImmediate(go);
            var result = _resolver.Resolve<GameObject>(RefOf(new JObject
            {
                ["kind"] = "gameObject",
                ["globalId"] = globalId,
            }));
            Assert.IsFalse(result.Ok);
            Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_UNITY_001));
        }

        [Test]
        public void ComponentRef_DestroyedComponentSlot_IsUnity001()
        {
            var go = Go("G2Owner");
            var animator = go.AddComponent<Animator>();
            UnityEngine.Object.DestroyImmediate(animator);
            var result = _resolver.Resolve<Animator>(RefOf(new JObject
            {
                ["kind"] = "component",
                ["owner"] = new JObject
                {
                    ["kind"] = "gameObject",
                    ["handle"] = SessionHandle.Write(go),
                },
                ["type"] = "unity.Animator",
                ["index"] = 0,
            }));
            Assert.IsFalse(result.Ok);
            Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_UNITY_001));
        }

        [Test]
        public void PrefabContentsRef_DestroyedContentsRoot_IsUnity003()
        {
            var doomed = Go("G2DoomedContents");
            var resolver = new RefResolver(_ => doomed);
            UnityEngine.Object.DestroyImmediate(doomed);
            // The Run-resource lookup still hands back the destroyed
            // contents root — a fake-null the resolver must read as absent.
            var result = resolver.Resolve<GameObject>(RefOf(new JObject
            {
                ["kind"] = "gameObject",
                ["scene"] = "prefab-contents:0123456789abcdef0123456789abcdef",
                ["path"] = "/G2DoomedContents",
            }));
            Assert.IsFalse(result.Ok);
            Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_UNITY_003));
        }

        [Test]
        public void PrefabContentsRef_DestroyedContentsRoot_SceneKeyIsUnity003()
        {
            var live = Go("G2Outside");
            var doomed = Go("G2DoomedContents");
            var resolver = new RefResolver(_ => doomed);
            UnityEngine.Object.DestroyImmediate(doomed);
            // The handle resolves a live object outside every contents
            // resource; the scene key names a registered resource whose
            // recorded root is now destroyed — unverifiable, OW-UNITY-003.
            var result = resolver.Resolve<GameObject>(RefOf(new JObject
            {
                ["kind"] = "gameObject",
                ["handle"] = SessionHandle.Write(live),
                ["scene"] = "prefab-contents:0123456789abcdef0123456789abcdef",
            }));
            Assert.IsFalse(result.Ok);
            Assert.That(CodeOf(result), Is.EqualTo(ErrorCodes.OW_UNITY_003));
        }

        [Test]
        public void ToRef_DestroyedObject_ThrowsArgumentNull()
        {
            var go = Go("G2Doomed");
            UnityEngine.Object.DestroyImmediate(go);
            Assert.Throws<ArgumentNullException>(
                () => _resolver.ToRef(go, "unity.GameObjectRef"));
        }

        [Test]
        public void LiveChildren_DestroyedParent_ReportsSceneRoots()
        {
            var parent = Go("G2DoomedParent");
            Go("G2DoomedChild", parent);
            var scene = parent.scene;
            var parentTransform = parent.transform;
            UnityEngine.Object.DestroyImmediate(parent);
            // A destroyed parent transform is absent evidence — the check
            // falls back to the scene's root list, never dereferences it.
            var children = GameObjectEvidence.LiveChildren(parentTransform, scene);
            CollectionAssert.AreEqual(scene.GetRootGameObjects(), children);
        }

        [Test]
        public void ComponentsMatch_DestroyedPrimitiveCollider_FailsClosed()
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _created.Add(cube);
            UnityEngine.Object.DestroyImmediate(cube.GetComponent<BoxCollider>());
            // The dead collider slot is unverifiable: the content check
            // fails closed instead of treating fake-null as a live collider.
            var matches = GameObjectEvidence.ComponentsMatch(
                cube, "cube", out var reason);
            Assert.IsFalse(matches);
            Assert.That(reason, Is.EqualTo("component list differs"));
        }
    }
}
