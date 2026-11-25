#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The Run-resource seam of SPEC §G2: answers the live contents root
    /// <see cref="GameObject"/> that the Run's <c>prefab_edit</c> resource
    /// (SPEC §E6 item 4, §F2 — opened by <c>unity.prefab.open_contents</c>,
    /// G6 item 5) holds for the prefab asset with GUID
    /// <paramref name="prefabAssetGuid"/>, or <see langword="null"/> when no
    /// such resource is open. This is the lookup a
    /// <c>prefab-contents:&lt;guid&gt;</c> scene key resolves through; the
    /// Runner wires it to the step's recorded Run resources
    /// (<see cref="OpWeave.Core.Persistence.RunResource"/>).
    /// </summary>
    public delegate GameObject? PrefabContentsLookup(string prefabAssetGuid);

    /// <summary>
    /// The production <see cref="IRefResolver"/> of SPEC §G2 (T-W2.3-01):
    /// resolves Ref JSON to Unity objects in the SPEC resolution order and
    /// writes Refs with every key the object can hold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Resolution.</b> The <c>kind</c> discriminator selects the §G2
    /// table row. Each row tries its keys in order and uses the first that
    /// finds an object: SceneRef guid → path (<c>GUIDToAssetPath</c>) →
    /// <c>SceneManager.GetSceneByPath</c>; GameObjectRef and
    /// PrefabInstanceRef handle (<see cref="SessionHandle"/>) → globalId
    /// (<c>GlobalObjectId.TryParse</c> +
    /// <c>GlobalObjectIdentifierToObjectSlow</c>) → scene + HierarchyPath;
    /// ComponentRef resolves its owner then <c>GetComponents(type)[index]</c>;
    /// AssetRef and PrefabAssetRef guid → path →
    /// <c>LoadAssetAtPath</c>/<c>LoadAllAssetsAtPath</c> for a
    /// <c>localId</c> sub-asset. Nothing is cached: a later call re-evaluates
    /// every key against the live state.
    /// </para>
    /// <para>
    /// <b>Key conflicts.</b> A resolved entity is checked against every
    /// other key the Ref carries: a key that resolves to a different
    /// identity than the entity's actual key is OW-UNITY-002 — the SPEC's
    /// example is a handle that lands on a different scene. A key that
    /// cannot be interpreted at all is unverifiable and skipped, per the
    /// "first key that finds an object wins" order.
    /// </para>
    /// <para>
    /// <b>Scenes.</b> The <c>scene</c> key is a nested SceneRef object or a
    /// string. A string of the form <c>prefab-contents:&lt;guid&gt;</c>
    /// resolves through <see cref="PrefabContentsLookup"/> — the Run
    /// resources of G6's <c>open_contents</c> — and any other string is a
    /// scene path or name. An unloaded scene is OW-UNITY-003; absent a
    /// scene key the path walks every loaded non-preview scene.
    /// </para>
    /// <para>
    /// <b>kind/T pairing.</b> The kind declares what the Ref names: a
    /// <c>scene</c> kind never yields a <see cref="UnityEngine.Object"/>
    /// (OW-UNITY-002; scenes resolve through <see cref="ResolveScene"/>),
    /// an <c>asset</c> kind resolves through the asset table row for
    /// every T SPEC §B5 allows — including <see cref="GameObject"/> and
    /// <see cref="Component"/>, which name objects inside a prefab
    /// asset — a <c>prefabAsset</c> kind is the
    /// <c>AssetRef&lt;unity.GameObject&gt;</c> of SPEC §B5 — the
    /// prefab's root asset — so a <see cref="GameObject"/> T resolves it
    /// through the asset table row, and any other incompatibility
    /// between kind, resolved entity and T is OW-UNITY-002.
    /// OW-UNITY-004 is the asset/instance mix-up: a GameObjectRef or
    /// PrefabInstanceRef landing on a persistent asset, a
    /// PrefabInstanceRef whose object is not the outermost prefab
    /// instance root, or a PrefabAssetRef that is not a prefab.
    /// </para>
    /// <para>
    /// <b>Local, reversible choices where SPEC leaves the shape open.</b>
    /// Scene resolution failure maps to OW-UNITY-003 for any unusable
    /// scene key — the <see cref="EditorRequirementEvaluator"/> reading;
    /// a ComponentRef without an owner, an unknown component class id and
    /// an out-of-range index are OW-UNITY-001 (the §G2 error set has no
    /// type-level code; OW-TYPE-003 covers only malformed Ref shape);
    /// a default <c>index</c> is 0 and a default <c>type</c> is
    /// <c>unity.Component</c>; a scene-key search without a
    /// <c>scene</c> value skips preview scenes — contents objects are
    /// reachable only through their <c>prefab-contents:</c> key.
    /// </para>
    /// </remarks>
    public sealed class RefResolver : IRefResolver
    {
        /// <summary>The scene-key prefix of G6 <c>open_contents</c> objects (SPEC §G2).</summary>
        internal const string PrefabContentsPrefix = "prefab-contents:";

        private static readonly string s_sceneKind = UnityShapeTypes.SceneKind;
        private static readonly string s_gameObjectKind = UnityShapeTypes.GameObjectKind;
        private static readonly string s_prefabInstanceKind = UnityShapeTypes.PrefabInstanceKind;
        private static readonly string s_componentKind = UnityShapeTypes.ComponentKind;
        private static readonly string s_assetKind = UnityShapeTypes.AssetKind;
        private static readonly string s_prefabAssetKind = UnityShapeTypes.PrefabAssetKind;

        private readonly PrefabContentsLookup? _prefabContentsRoots;

        /// <summary>
        /// A resolver whose <c>prefab-contents:</c> scene keys resolve
        /// through <paramref name="prefabContentsRoots"/> — null until the
        /// Runner wires the Run-resource lookup; a prefab-contents Ref then
        /// always reports OW-UNITY-003.
        /// </summary>
        public RefResolver(PrefabContentsLookup? prefabContentsRoots = null)
        {
            _prefabContentsRoots = prefabContentsRoots;
        }

        /// <inheritdoc/>
        /// <exception cref="ArgumentNullException"><paramref name="refJson"/> is null.</exception>
        public Result<T> Resolve<T>(JsonValue refJson) where T : UnityEngine.Object
        {
            if (refJson is null)
            {
                throw new ArgumentNullException(nameof(refJson));
            }
            if (refJson.ToToken() is not JObject obj)
            {
                return Fail<T>(ErrorCodes.OW_TYPE_003,
                    "The Ref value is not a JSON object.", "$");
            }
            var kind = StringMember(obj, "kind");
            if (kind is null)
            {
                return Fail<T>(ErrorCodes.OW_TYPE_003,
                    "The Ref object has no 'kind' discriminator.", "$.kind");
            }
            switch (kind)
            {
                case UnityShapeTypes.SceneKind:
                    return Fail<T>(ErrorCodes.OW_UNITY_002,
                        "Ref kind 'scene' names a scene, not a UnityEngine.Object.", "$.kind");
                case UnityShapeTypes.GameObjectKind:
                case UnityShapeTypes.PrefabInstanceKind:
                {
                    if (!typeof(T).IsAssignableFrom(typeof(GameObject)))
                    {
                        return Fail<T>(ErrorCodes.OW_UNITY_002,
                            $"Ref kind '{kind}' names a scene GameObject, which {typeof(T).Name} cannot hold.",
                            "$.kind");
                    }
                    var resolved = ResolveGameObject(
                        obj, kind == UnityShapeTypes.PrefabInstanceKind, "$");
                    return resolved.Ok
                        ? Result<T>.Success((T)(object)resolved.Value!)
                        : Result<T>.Failure(resolved.Diagnostics);
                }
                case UnityShapeTypes.ComponentKind:
                {
                    if (typeof(GameObject).IsAssignableFrom(typeof(T)))
                    {
                        return Fail<T>(ErrorCodes.OW_UNITY_002,
                            $"Ref kind 'component' names a Component, which {typeof(T).Name} cannot hold.",
                            "$.kind");
                    }
                    return ResolveComponent<T>(obj, "$");
                }
                case UnityShapeTypes.AssetKind:
                {
                    // SPEC §G2: AssetRef<T> resolves guid → path →
                    // LoadAssetAtPath<T> (localId via LoadAllAssetsAtPath)
                    // for every T §B5 allows — including GameObject and
                    // Component, which name objects inside a prefab asset.
                    return ResolveAsset<T>(obj, prefabAsset: false, jsonPath: "$");
                }
                case UnityShapeTypes.PrefabAssetKind:
                {
                    // SPEC §B5: PrefabAssetRef is AssetRef<unity.GameObject>
                    // — the ref names the prefab asset's root, so a
                    // GameObject T resolves through the asset table row.
                    // OW-UNITY-004 applies when the resolved asset is not
                    // a prefab, not to the GameObject T itself (§G2).
                    if (!typeof(T).IsAssignableFrom(typeof(GameObject)))
                    {
                        return Fail<T>(ErrorCodes.OW_UNITY_002,
                            $"Ref kind 'prefabAsset' names a prefab asset, which {typeof(T).Name} cannot hold.",
                            "$.kind");
                    }
                    return ResolveAsset<T>(obj, prefabAsset: true, jsonPath: "$");
                }
                default:
                    return Fail<T>(ErrorCodes.OW_TYPE_003,
                        $"Ref kind '{kind}' is not a known ref kind.", "$.kind");
            }
        }

        /// <inheritdoc/>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="obj"/> is null or destroyed, or
        /// <paramref name="refTypeId"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="refTypeId"/> is not a SPEC §B5 Ref Type ID, or the
        /// (object, refTypeId) pair is incompatible — a programming error,
        /// not a runtime diagnostic.
        /// </exception>
        public JsonValue ToRef(UnityEngine.Object obj, string refTypeId)
        {
            if (obj == null)
            {
                throw new ArgumentNullException(nameof(obj));
            }
            if (refTypeId is null)
            {
                throw new ArgumentNullException(nameof(refTypeId));
            }
            switch (refTypeId)
            {
                case "unity.ObjectRef":
                    return ObjectRefOf(obj);
                case "unity.SceneRef":
                    return SceneRefOf(AsSceneAsset(obj, refTypeId));
                case "unity.GameObjectRef":
                    return GameObjectRefOf(obj, prefabInstance: false);
                case "unity.PrefabInstanceRef":
                    return GameObjectRefOf(obj, prefabInstance: true);
                case "unity.PrefabAssetRef":
                    return PrefabAssetRefOf(obj);
            }
            var componentArg = GenericArg(refTypeId, "unity.ComponentRef");
            if (componentArg is not null)
            {
                return ComponentRefOf(obj, componentArg);
            }
            var assetArg = GenericArg(refTypeId, "unity.AssetRef");
            if (assetArg is not null)
            {
                return AssetRefOf(obj, assetArg);
            }
            throw new ArgumentException(
                $"Not a SPEC §B5 ref type id: {refTypeId}", nameof(refTypeId));
        }

        /// <summary>
        /// Resolves a SceneRef value — a <c>{kind:"scene",…}</c> object, or a
        /// bare scene path/name string — to the loaded scene it names (SPEC
        /// §G2: guid → path → <c>GetSceneByPath</c>). Scene is a struct, so
        /// <see cref="Resolve{T}"/> cannot carry it; Operations take this
        /// path through the concrete class.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="sceneRef"/> is null.</exception>
        internal Result<Scene> ResolveScene(JsonValue sceneRef)
        {
            if (sceneRef is null)
            {
                throw new ArgumentNullException(nameof(sceneRef));
            }
            return ResolveSceneToken(sceneRef.ToToken(), "$");
        }

        // ── GameObjectRef / PrefabInstanceRef ────────────────────────────

        private Result<GameObject> ResolveGameObject(
            JObject obj, bool prefabInstance, string jsonPath)
        {
            var handleClaimed = false;
            var claimedHandle = 0UL;
            if (obj["handle"] is { } handleToken)
            {
                if (handleToken is not (JValue { Type: JTokenType.Integer } or
                    JValue { Type: JTokenType.String }))
                {
                    return Fail<GameObject>(ErrorCodes.OW_TYPE_003,
                        "Ref 'handle' is not an integer or a digit string.",
                        Member(jsonPath, "handle"));
                }
                // A syntactically valid handle outside this generation's key
                // space claims an identity that cannot exist — unverifiable,
                // so it is skipped like any unfound key.
                handleClaimed = SessionHandle.TryRead(handleToken, out claimedHandle);
            }
            var globalId = StringMemberChecked(obj, "globalId", jsonPath, out var globalIdError);
            if (globalIdError is not null)
            {
                return Result<GameObject>.Failure(globalIdError);
            }
            var path = StringMemberChecked(obj, "path", jsonPath, out var pathError);
            if (pathError is not null)
            {
                return Result<GameObject>.Failure(pathError);
            }
            var prefabAsset = StringMemberChecked(obj, "prefabAsset", jsonPath, out var prefabError);
            if (prefabError is not null)
            {
                return Result<GameObject>.Failure(prefabError);
            }
            var sceneToken = obj["scene"];

            GameObject? resolved = null;
            if (handleClaimed)
            {
                var viaHandle = SessionHandle.Resolve(claimedHandle);
                if (viaHandle != null)
                {
                    if (viaHandle is GameObject viaHandleGo)
                    {
                        resolved = viaHandleGo;
                    }
                    else
                    {
                        return Fail<GameObject>(ErrorCodes.OW_UNITY_002,
                            "The ref's handle names an object that is not a GameObject.",
                            Member(jsonPath, "handle"));
                    }
                }
            }
            if (resolved == null && globalId is not null &&
                GlobalObjectId.TryParse(globalId, out var parsedId))
            {
                var viaGlobal = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(parsedId);
                if (viaGlobal != null)
                {
                    if (viaGlobal is GameObject viaGlobalGo)
                    {
                        resolved = viaGlobalGo;
                    }
                    else
                    {
                        return Fail<GameObject>(ErrorCodes.OW_UNITY_002,
                            "The ref's globalId names an object that is not a GameObject.",
                            Member(jsonPath, "globalId"));
                    }
                }
            }
            if (resolved == null && path is not null)
            {
                if (!RefHierarchyPath.TryParse(path, out var segments))
                {
                    return Fail<GameObject>(ErrorCodes.OW_TYPE_003,
                        "Ref 'path' is not a hierarchy path ('/Root/Child').",
                        Member(jsonPath, "path"));
                }
                var scope = ResolveScope(sceneToken, Member(jsonPath, "scene"));
                if (!scope.Ok)
                {
                    return Result<GameObject>.Failure(scope.Diagnostics);
                }
                var walked = WalkSegments(scope.Value!, segments, Member(jsonPath, "path"));
                if (!walked.Ok)
                {
                    return Result<GameObject>.Failure(walked.Diagnostics);
                }
                resolved = walked.Value!;
            }
            if (resolved == null)
            {
                return Fail<GameObject>(ErrorCodes.OW_UNITY_001,
                    "No key of the ref resolves to a live GameObject.", jsonPath);
            }

            // The ref kind claims a scene object: a persistent asset where a
            // scene GameObject belongs is the §G2 asset/instance mix-up.
            if (!resolved.scene.IsValid() || EditorUtility.IsPersistent(resolved))
            {
                return Fail<GameObject>(ErrorCodes.OW_UNITY_004,
                    "The ref resolved to a persistent asset, not a scene GameObject.",
                    jsonPath);
            }
            if (prefabInstance && !PrefabUtility.IsOutermostPrefabInstanceRoot(resolved))
            {
                return Fail<GameObject>(ErrorCodes.OW_UNITY_004,
                    "The resolved GameObject is not the outermost prefab instance root.",
                    jsonPath);
            }

            // Every other present key must name the resolved entity (SPEC
            // §G2: a resolved entity contradicting another key is
            // OW-UNITY-002, e.g. a handle whose object sits in a different
            // scene).
            if (handleClaimed && SessionHandle.RawOf(resolved) != claimedHandle)
            {
                return Fail<GameObject>(ErrorCodes.OW_UNITY_002,
                    "The ref's handle and the resolved GameObject disagree.",
                    Member(jsonPath, "handle"));
            }
            if (globalId is not null &&
                !string.Equals(
                    GlobalObjectId.GetGlobalObjectIdSlow(resolved).ToString(),
                    globalId, StringComparison.Ordinal))
            {
                return Fail<GameObject>(ErrorCodes.OW_UNITY_002,
                    "The ref's globalId and the resolved GameObject disagree.",
                    Member(jsonPath, "globalId"));
            }
            if (sceneToken is not null &&
                SceneKeyConflict(resolved, sceneToken, Member(jsonPath, "scene"))
                    is { } sceneConflict)
            {
                return Result<GameObject>.Failure(sceneConflict);
            }
            if (path is not null && !ClaimedPathSelects(path, resolved))
            {
                return Fail<GameObject>(ErrorCodes.OW_UNITY_002,
                    "The ref's path and the resolved GameObject's hierarchy path disagree.",
                    Member(jsonPath, "path"));
            }
            if (prefabAsset is not null)
            {
                var sourcePath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(resolved);
                var actualGuid = string.IsNullOrEmpty(sourcePath)
                    ? null
                    : AssetDatabase.AssetPathToGUID(sourcePath);
                if (!string.Equals(actualGuid, prefabAsset, StringComparison.Ordinal))
                {
                    return Fail<GameObject>(ErrorCodes.OW_UNITY_002,
                        "The ref's prefabAsset guid and the resolved instance's source prefab disagree.",
                        Member(jsonPath, "prefabAsset"));
                }
            }
            return Result<GameObject>.Success(resolved);
        }

        /// <summary>
        /// The "scene + HierarchyPath" scope of SPEC §G2: no
        /// <c>scene</c> key searches the roots of every loaded non-preview
        /// scene; a <c>prefab-contents:&lt;guid&gt;</c> string resolves
        /// through the Run resources (<see cref="PrefabContentsLookup"/>);
        /// any other scene value resolves to its loaded scene's roots.
        /// </summary>
        private Result<IReadOnlyList<GameObject>> ResolveScope(JToken? sceneToken, string jsonPath)
        {
            if (sceneToken is null)
            {
                var roots = new List<GameObject>();
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (scene.isLoaded && !EditorSceneManager.IsPreviewScene(scene))
                    {
                        roots.AddRange(scene.GetRootGameObjects());
                    }
                }
                return Result<IReadOnlyList<GameObject>>.Success(roots);
            }
            if (sceneToken is JValue { Type: JTokenType.String } text)
            {
                var sceneName = (string)text.Value!;
                if (sceneName.StartsWith(PrefabContentsPrefix, StringComparison.Ordinal))
                {
                    var guid = sceneName.Substring(PrefabContentsPrefix.Length);
                    var root = _prefabContentsRoots?.Invoke(guid);
                    if (root == null)
                    {
                        return Fail<IReadOnlyList<GameObject>>(ErrorCodes.OW_UNITY_003,
                            "No open prefab contents resource matches the ref's scene.",
                            jsonPath);
                    }
                    return Result<IReadOnlyList<GameObject>>.Success(
                        new List<GameObject> { root });
                }
                var byName = ResolveSceneString(sceneName, jsonPath);
                return byName.Ok
                    ? Result<IReadOnlyList<GameObject>>.Success(byName.Value!.GetRootGameObjects())
                    : Result<IReadOnlyList<GameObject>>.Failure(byName.Diagnostics);
            }
            if (sceneToken is JObject sceneObj)
            {
                var scene = ResolveSceneObject(sceneObj, jsonPath);
                return scene.Ok
                    ? Result<IReadOnlyList<GameObject>>.Success(scene.Value!.GetRootGameObjects())
                    : Result<IReadOnlyList<GameObject>>.Failure(scene.Diagnostics);
            }
            return Fail<IReadOnlyList<GameObject>>(ErrorCodes.OW_TYPE_003,
                "Ref 'scene' is not a scene reference or a scene path.", jsonPath);
        }

        // SPEC §G2 HierarchyPath: a segment without "[n]" must name exactly
        // one sibling — same-name siblings without an index are
        // OW-UNITY-002; a missing name or index is OW-UNITY-001.
        private static Result<GameObject> WalkSegments(
            IReadOnlyList<GameObject> roots,
            RefHierarchyPath.Segment[] segments,
            string jsonPath)
        {
            var level = roots;
            GameObject? node = null;
            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                var sameName = new List<GameObject>();
                for (var j = 0; j < level.Count; j++)
                {
                    if (level[j].name == segment.Name)
                    {
                        sameName.Add(level[j]);
                    }
                }
                if (segment.Index >= 0)
                {
                    if (segment.Index >= sameName.Count)
                    {
                        return Fail<GameObject>(ErrorCodes.OW_UNITY_001,
                            $"Hierarchy path segment '{segment.Name}[{segment.Index}]' matches no object.",
                            jsonPath);
                    }
                    node = sameName[segment.Index];
                }
                else if (sameName.Count == 0)
                {
                    return Fail<GameObject>(ErrorCodes.OW_UNITY_001,
                        $"Hierarchy path segment '{segment.Name}' matches no object.",
                        jsonPath);
                }
                else if (sameName.Count > 1)
                {
                    return Fail<GameObject>(ErrorCodes.OW_UNITY_002,
                        $"Hierarchy path segment '{segment.Name}' is ambiguous: " +
                        $"{sameName.Count} siblings share the name.",
                        jsonPath);
                }
                else
                {
                    node = sameName[0];
                }
                var transform = node!.transform;
                var children = new List<GameObject>(transform.childCount);
                for (var j = 0; j < transform.childCount; j++)
                {
                    children.Add(transform.GetChild(j).gameObject);
                }
                level = children;
            }
            return Result<GameObject>.Success(node!);
        }

        // The 'path' key names the resolved entity when walking its
        // segments inside the object's own scene selects that object: an
        // explicit "[0]" on a unique name is the same target as the bare
        // name RefHierarchyPath.For emits, while a bare name with
        // same-name siblings or an out-of-range index still contradicts
        // the resolved entity.
        private static bool ClaimedPathSelects(string path, GameObject resolved)
        {
            if (!RefHierarchyPath.TryParse(path, out var segments))
            {
                return false;
            }
            var walked = WalkSegments(
                resolved.scene.GetRootGameObjects(), segments, "$");
            return walked.Ok && walked.Value == resolved;
        }

        // Whether the resolved GameObject's scene satisfies the ref's
        // 'scene' key, or the diagnostic when it does not. A
        // "prefab-contents:<guid>" string resolves through the Run
        // resources (PrefabContentsLookup) whichever higher-priority key
        // found the object: no registered resource is OW-UNITY-003 and an
        // object outside the returned contents root is OW-UNITY-002.
        // Another string matches the scene path or name; a nested
        // SceneRef's guid/path must equal the object's own.
        private Diagnostic? SceneKeyConflict(
            GameObject go, JToken sceneToken, string jsonPath)
        {
            var scene = go.scene;
            if (sceneToken is JValue { Type: JTokenType.String } text)
            {
                var name = (string)text.Value!;
                if (name.StartsWith(PrefabContentsPrefix, StringComparison.Ordinal))
                {
                    var root = _prefabContentsRoots?.Invoke(
                        name.Substring(PrefabContentsPrefix.Length));
                    if (root == null)
                    {
                        return ErrorCodes.Create(
                            ErrorCodes.OW_UNITY_003,
                            "No open prefab contents resource matches the ref's scene.",
                            jsonPath: jsonPath);
                    }
                    return go == root || go.transform.IsChildOf(root.transform)
                        ? null
                        : SceneKeyConflict002(jsonPath);
                }
                return string.Equals(scene.path, name, StringComparison.Ordinal) ||
                    string.Equals(scene.name, name, StringComparison.Ordinal)
                        ? null
                        : SceneKeyConflict002(jsonPath);
            }
            if (sceneToken is JObject sceneObj)
            {
                var guid = StringMember(sceneObj, "guid");
                if (guid is not null)
                {
                    var guidPath = AssetDatabase.GUIDToAssetPath(guid);
                    var matches = guidPath.Length > 0
                        ? string.Equals(scene.path, guidPath, StringComparison.Ordinal)
                        : string.Equals(
                            AssetDatabase.AssetPathToGUID(scene.path), guid,
                            StringComparison.Ordinal);
                    if (!matches)
                    {
                        return SceneKeyConflict002(jsonPath);
                    }
                }
                var path = StringMember(sceneObj, "path");
                if (path is not null &&
                    !string.Equals(scene.path, path, StringComparison.Ordinal))
                {
                    return SceneKeyConflict002(jsonPath);
                }
            }
            return null;
        }

        private static Diagnostic SceneKeyConflict002(string jsonPath) =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_002,
                "The ref's scene and the resolved GameObject's scene disagree.",
                jsonPath: jsonPath);

        // ── SceneRef ──────────────────────────────────────────────────────

        private Result<Scene> ResolveSceneToken(JToken token, string jsonPath)
        {
            switch (token)
            {
                case JObject sceneObj:
                    return ResolveSceneObject(sceneObj, jsonPath);
                case JValue { Type: JTokenType.String } text:
                    return ResolveSceneString((string)text.Value!, jsonPath);
                default:
                    return Fail<Scene>(ErrorCodes.OW_TYPE_003,
                        "The scene ref is not a scene reference object or a scene path.",
                        jsonPath);
            }
        }

        private Result<Scene> ResolveSceneString(string name, string jsonPath)
        {
            var byPath = SceneManager.GetSceneByPath(name);
            if (byPath.isLoaded)
            {
                return Result<Scene>.Success(byPath);
            }
            var byName = SceneManager.GetSceneByName(name);
            if (byName.isLoaded)
            {
                return Result<Scene>.Success(byName);
            }
            return Fail<Scene>(ErrorCodes.OW_UNITY_003,
                $"The scene '{name}' is not loaded.", jsonPath);
        }

        // SPEC §G2 SceneRef: guid resolves to an asset path first; the path
        // key is consulted only when the guid names nothing, and a guid that
        // resolves elsewhere conflicts with a present path key (OW-UNITY-002).
        // A path that names no loaded scene is OW-UNITY-003 — the
        // EditorRequirementEvaluator reading of an unusable scene key.
        private Result<Scene> ResolveSceneObject(JObject sceneObj, string jsonPath)
        {
            var guid = StringMemberChecked(sceneObj, "guid", jsonPath, out var guidError);
            if (guidError is not null)
            {
                return Result<Scene>.Failure(guidError);
            }
            var path = StringMemberChecked(sceneObj, "path", jsonPath, out var pathError);
            if (pathError is not null)
            {
                return Result<Scene>.Failure(pathError);
            }
            var guidPath = guid is null ? null : AssetDatabase.GUIDToAssetPath(guid);
            var guidResolved = !string.IsNullOrEmpty(guidPath);
            if (guidResolved && path is not null &&
                !string.Equals(guidPath, path, StringComparison.Ordinal))
            {
                return Fail<Scene>(ErrorCodes.OW_UNITY_002,
                    "The scene ref's guid and path name different assets.",
                    Member(jsonPath, "path"));
            }
            var target = guidResolved ? guidPath! : path;
            if (target is null)
            {
                return Fail<Scene>(ErrorCodes.OW_UNITY_003,
                    "No key of the scene ref identifies a scene.", jsonPath);
            }
            if (guid is not null && !guidResolved &&
                !string.Equals(AssetDatabase.AssetPathToGUID(target), guid, StringComparison.Ordinal))
            {
                return Fail<Scene>(ErrorCodes.OW_UNITY_002,
                    "The scene ref's guid and the path's actual guid disagree.",
                    Member(jsonPath, "guid"));
            }
            var scene = SceneManager.GetSceneByPath(target);
            if (!scene.isLoaded)
            {
                return Fail<Scene>(ErrorCodes.OW_UNITY_003,
                    $"The scene '{target}' is not loaded.", jsonPath);
            }
            return Result<Scene>.Success(scene);
        }

        // ── ComponentRef ──────────────────────────────────────────────────

        // SPEC §G2 ComponentRef: resolve the owner GameObjectRef, then
        // GetComponents(type)[index]. 'type' defaults to any Component and
        // 'index' to 0 (documented local choices).
        private Result<T> ResolveComponent<T>(JObject obj, string jsonPath)
            where T : UnityEngine.Object
        {
            if (obj["owner"] is not { } ownerToken)
            {
                return Fail<T>(ErrorCodes.OW_UNITY_001,
                    "The component ref carries no owner to resolve.", jsonPath);
            }
            if (ownerToken is not JObject owner)
            {
                return Fail<T>(ErrorCodes.OW_TYPE_003,
                    "Ref 'owner' is not a GameObject reference.",
                    Member(jsonPath, "owner"));
            }
            Result<GameObject> ownerResult;
            var ownerKind = StringMember(owner, "kind");
            if (ownerKind == s_gameObjectKind)
            {
                ownerResult = ResolveGameObject(owner, prefabInstance: false,
                    jsonPath: Member(jsonPath, "owner"));
            }
            else if (ownerKind == s_prefabInstanceKind)
            {
                ownerResult = ResolveGameObject(owner, prefabInstance: true,
                    jsonPath: Member(jsonPath, "owner"));
            }
            else
            {
                return Fail<T>(ErrorCodes.OW_UNITY_002,
                    "The component ref's owner is not a GameObjectRef.",
                    Member(jsonPath, "owner"));
            }
            if (!ownerResult.Ok)
            {
                return Result<T>.Failure(ownerResult.Diagnostics);
            }
            var typeName = StringMemberChecked(obj, "type", jsonPath, out var typeError);
            if (typeError is not null)
            {
                return Result<T>.Failure(typeError);
            }
            Type? componentType = null;
            if (typeName is not null)
            {
                componentType = UnityClassRelationProvider.ResolveClassId(typeName);
                if (componentType is null)
                {
                    return Fail<T>(ErrorCodes.OW_UNITY_001,
                        $"The component type '{typeName}' was not found.",
                        Member(jsonPath, "type"));
                }
                if (!typeof(Component).IsAssignableFrom(componentType))
                {
                    return Fail<T>(ErrorCodes.OW_UNITY_001,
                        $"The type '{typeName}' is not a Component type.",
                        Member(jsonPath, "type"));
                }
            }
            var index = 0;
            if (obj["index"] is { } indexToken)
            {
                if (indexToken is not JValue { Type: JTokenType.Integer } indexValue ||
                    (long)indexValue.Value! < 0 || (long)indexValue.Value! > int.MaxValue)
                {
                    return Fail<T>(ErrorCodes.OW_TYPE_003,
                        "Ref 'index' is not a non-negative integer.",
                        Member(jsonPath, "index"));
                }
                index = (int)(long)indexValue.Value!;
            }
            var components = ownerResult.Value!.GetComponents(componentType ?? typeof(Component));
            if (index >= components.Length)
            {
                return Fail<T>(ErrorCodes.OW_UNITY_001,
                    $"No component of type '{typeName ?? "unity.Component"}' exists at index {index}.",
                    Member(jsonPath, "index"));
            }
            var selected = components[index];
            if (selected == null)
            {
                return Fail<T>(ErrorCodes.OW_UNITY_001,
                    $"No component of type '{typeName ?? "unity.Component"}' exists at index {index}.",
                    Member(jsonPath, "index"));
            }
            if (selected is T typed)
            {
                return Result<T>.Success(typed);
            }
            return Fail<T>(ErrorCodes.OW_UNITY_002,
                $"The resolved component of type '{selected.GetType().Name}' " +
                $"is not a {typeof(T).Name}.", jsonPath);
        }

        // ── AssetRef / PrefabAssetRef ─────────────────────────────────────

        // SPEC §G2 AssetRef: guid → path → LoadAssetAtPath<T>, and a
        // 'localId' key selects the matching member of
        // LoadAllAssetsAtPath. A guid resolving elsewhere than a present
        // path is OW-UNITY-002; a prefabAsset kind resolving to a non-prefab
        // asset is OW-UNITY-004.
        private Result<T> ResolveAsset<T>(JObject obj, bool prefabAsset, string jsonPath)
            where T : UnityEngine.Object
        {
            var guid = StringMemberChecked(obj, "guid", jsonPath, out var guidError);
            if (guidError is not null)
            {
                return Result<T>.Failure(guidError);
            }
            var path = StringMemberChecked(obj, "path", jsonPath, out var pathError);
            if (pathError is not null)
            {
                return Result<T>.Failure(pathError);
            }
            long? localId = null;
            if (obj["localId"] is { } localIdToken)
            {
                if (localIdToken is not JValue { Type: JTokenType.Integer } localIdValue)
                {
                    return Fail<T>(ErrorCodes.OW_TYPE_003,
                        "Ref 'localId' is not an integer.", Member(jsonPath, "localId"));
                }
                localId = (long)localIdValue.Value!;
            }
            var guidPath = guid is null ? null : AssetDatabase.GUIDToAssetPath(guid);
            var guidResolved = !string.IsNullOrEmpty(guidPath);
            if (guidResolved && path is not null &&
                !string.Equals(guidPath, path, StringComparison.Ordinal))
            {
                return Fail<T>(ErrorCodes.OW_UNITY_002,
                    "The asset ref's guid and path name different assets.",
                    Member(jsonPath, "path"));
            }
            var target = guidResolved ? guidPath! : path;
            if (target is null)
            {
                return Fail<T>(ErrorCodes.OW_UNITY_001,
                    "No key of the asset ref names an asset path.", jsonPath);
            }
            if (guid is not null && !guidResolved &&
                !string.Equals(AssetDatabase.AssetPathToGUID(target), guid, StringComparison.Ordinal))
            {
                return Fail<T>(ErrorCodes.OW_UNITY_002,
                    "The asset ref's guid and the path's actual guid disagree.",
                    Member(jsonPath, "guid"));
            }

            UnityEngine.Object? resolved;
            if (localId.HasValue)
            {
                resolved = null;
                foreach (var candidate in AssetDatabase.LoadAllAssetsAtPath(target))
                {
                    if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                        candidate, out _, out var candidateId) &&
                        candidateId == localId.Value)
                    {
                        resolved = candidate;
                        break;
                    }
                }
                if (resolved == null)
                {
                    return Fail<T>(ErrorCodes.OW_UNITY_001,
                        $"No asset with localId {localId.Value} exists at '{target}'.",
                        Member(jsonPath, "localId"));
                }
            }
            else
            {
                // SPEC §G2: LoadAssetAtPath<T> picks the first object of
                // the requested type at the path, not only the main
                // asset. A null result separates a missing asset
                // (OW-UNITY-001) from assets of only other types
                // (OW-UNITY-002).
                resolved = AssetDatabase.LoadAssetAtPath<T>(target);
                if (resolved == null)
                {
                    var main = AssetDatabase.LoadMainAssetAtPath(target);
                    return main != null
                        ? Fail<T>(ErrorCodes.OW_UNITY_002,
                            $"The assets at '{target}' hold no {typeof(T).Name}; " +
                            $"the main asset is a {main.GetType().Name}.",
                            jsonPath)
                        : Fail<T>(ErrorCodes.OW_UNITY_001,
                            $"No asset exists at '{target}'.", jsonPath);
                }
            }
            if (resolved is not T)
            {
                return Fail<T>(ErrorCodes.OW_UNITY_002,
                    $"The asset at '{target}' is a {resolved.GetType().Name}, not a {typeof(T).Name}.",
                    jsonPath);
            }
            if (prefabAsset && PrefabUtility.GetPrefabAssetType(resolved) == PrefabAssetType.NotAPrefab)
            {
                return Fail<T>(ErrorCodes.OW_UNITY_004,
                    $"The asset at '{target}' is not a prefab.", jsonPath);
            }
            return Result<T>.Success((T)resolved);
        }

        // ── ToRef ─────────────────────────────────────────────────────────

        private JsonValue ObjectRefOf(UnityEngine.Object obj)
        {
            switch (obj)
            {
                case SceneAsset sceneAsset:
                    return SceneRefOf(sceneAsset);
                case GameObject go when go.scene.IsValid():
                    return GameObjectRefOf(
                        go, PrefabUtility.IsOutermostPrefabInstanceRoot(go));
                case GameObject _:
                    return PrefabAssetRefOf(obj);
                case Component component:
                    return ComponentRefOf(component, null);
                default:
                    return AssetRefOf(obj, null);
            }
        }

        private static JsonValue SceneRefOf(SceneAsset sceneAsset)
        {
            var path = AssetDatabase.GetAssetPath(sceneAsset);
            return JsonValue.From(new JObject
            {
                ["kind"] = s_sceneKind,
                ["path"] = path,
                ["guid"] = AssetDatabase.AssetPathToGUID(path),
            });
        }

        // SPEC §G2 "fill every key the object can hold": the scene key as a
        // fully keyed SceneRef (or "prefab-contents:<guid>" inside opened
        // prefab contents), the HierarchyPath, the globalId where the
        // object's scene gives it a stable one — never on an unsaved
        // (Untitled) scene, where SPEC §G2 permits only handle and
        // HierarchyPath — and the session handle.
        private static JsonValue GameObjectRefOf(UnityEngine.Object obj, bool prefabInstance)
        {
            var go = obj as GameObject;
            if (go == null)
            {
                throw new ArgumentException(
                    "The object is not a GameObject.", nameof(obj));
            }
            if (!go.scene.IsValid() || EditorUtility.IsPersistent(go))
            {
                throw new ArgumentException(
                    "The object is a persistent asset, not a scene GameObject; " +
                    "use an asset ref type.", nameof(obj));
            }
            if (prefabInstance && !PrefabUtility.IsOutermostPrefabInstanceRoot(go))
            {
                throw new ArgumentException(
                    "The object is not the outermost prefab instance root.",
                    nameof(obj));
            }
            var scene = go.scene;
            var result = new JObject
            {
                ["kind"] = prefabInstance ? s_prefabInstanceKind : s_gameObjectKind,
            };
            if (EditorSceneManager.IsPreviewScene(scene))
            {
                result["scene"] =
                    PrefabContentsPrefix + AssetDatabase.AssetPathToGUID(scene.path);
            }
            else if (scene.path.Length > 0)
            {
                result["scene"] = SceneRefValue(scene.path);
            }
            result["path"] = RefHierarchyPath.For(go);
            var globalId = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString();
            if (GlobalObjectId.TryParse(globalId, out _))
            {
                result["globalId"] = globalId;
            }
            result["handle"] = SessionHandle.Write(go);
            if (prefabInstance)
            {
                var sourcePath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                result["prefabAsset"] = AssetDatabase.AssetPathToGUID(sourcePath);
            }
            return JsonValue.From(result);
        }

        private static JObject SceneRefValue(string scenePath) =>
            new JObject
            {
                ["kind"] = s_sceneKind,
                ["path"] = scenePath,
                ["guid"] = AssetDatabase.AssetPathToGUID(scenePath),
            };

        private static JsonValue ComponentRefOf(UnityEngine.Object obj, string? typeArg)
        {
            var component = obj as Component;
            if (component == null)
            {
                throw new ArgumentException(
                    "The object is not a Component.", nameof(obj));
            }
            var go = component.gameObject;
            if (!go.scene.IsValid() || EditorUtility.IsPersistent(go))
            {
                throw new ArgumentException(
                    "The Component belongs to a persistent asset; a ComponentRef " +
                    "names a scene object.", nameof(obj));
            }
            CheckClassArg(component.GetType(), typeArg);
            var components = go.GetComponents(component.GetType());
            return JsonValue.From(new JObject
            {
                ["kind"] = s_componentKind,
                ["owner"] = (JObject)GameObjectRefOf(
                    go, PrefabUtility.IsOutermostPrefabInstanceRoot(go)).ToToken(),
                ["type"] = UnityClassRelationProvider.ClassIdOf(component.GetType()),
                ["index"] = System.Array.IndexOf(components, component),
            });
        }

        private static JsonValue AssetRefOf(UnityEngine.Object obj, string? typeArg)
        {
            if (!EditorUtility.IsPersistent(obj))
            {
                throw new ArgumentException(
                    "The object is not a persistent asset.", nameof(obj));
            }
            CheckClassArg(obj.GetType(), typeArg);
            var path = AssetDatabase.GetAssetPath(obj);
            var result = new JObject
            {
                // SPEC §B5: the kind is the Ref Type ID's discriminator, not
                // the object's CLR type — AssetRef<T> is 'asset' even for a
                // GameObject asset; 'prefabAsset' is PrefabAssetRefOf's kind
                // and its key set does not admit 'localId'.
                ["kind"] = s_assetKind,
                ["guid"] = AssetDatabase.AssetPathToGUID(path),
                ["path"] = path,
            };
            if (!AssetDatabase.IsMainAsset(obj) &&
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out _, out var localId))
            {
                result["localId"] = localId;
            }
            return JsonValue.From(result);
        }

        private static JsonValue PrefabAssetRefOf(UnityEngine.Object obj)
        {
            if (obj is not GameObject ||
                PrefabUtility.GetPrefabAssetType(obj) == PrefabAssetType.NotAPrefab)
            {
                throw new ArgumentException(
                    "The object is not a prefab asset.", nameof(obj));
            }
            var path = AssetDatabase.GetAssetPath(obj);
            return JsonValue.From(new JObject
            {
                ["kind"] = s_prefabAssetKind,
                ["guid"] = AssetDatabase.AssetPathToGUID(path),
                ["path"] = path,
            });
        }

        private static SceneAsset AsSceneAsset(UnityEngine.Object obj, string refTypeId)
        {
            if (obj is not SceneAsset sceneAsset)
            {
                throw new ArgumentException(
                    $"The object is not a SceneAsset for ref type {refTypeId}.",
                    nameof(obj));
            }
            return sceneAsset;
        }

        // The generic argument of "unity.ComponentRef<…>" / "unity.AssetRef<…>",
        // or null when the id is not that generic spelling.
        private static string? GenericArg(string refTypeId, string definitionId)
        {
            var prefix = definitionId + "<";
            return refTypeId.StartsWith(prefix, StringComparison.Ordinal) &&
                refTypeId.EndsWith(">", StringComparison.Ordinal)
                    ? refTypeId.Substring(prefix.Length, refTypeId.Length - prefix.Length - 1)
                    : null;
        }

        // The Ref Type ID's class-id argument must resolve and cover the
        // object's actual type — a caller bug otherwise.
        private static void CheckClassArg(Type actualType, string? classIdArg)
        {
            if (classIdArg is null)
            {
                return;
            }
            var expected = UnityClassRelationProvider.ResolveClassId(classIdArg);
            if (expected is null || !expected.IsAssignableFrom(actualType))
            {
                throw new ArgumentException(
                    $"The object's type {actualType.FullName} does not satisfy " +
                    $"the ref type's class id '{classIdArg}'.", nameof(classIdArg));
            }
        }

        // ── helpers ───────────────────────────────────────────────────────

        private static string? StringMember(JObject obj, string name) =>
            obj[name] is JValue { Type: JTokenType.String } value
                ? (string?)value.Value
                : null;

        // A string member that reports a non-string presence as the
        // OW-TYPE-003 shape failure the Codec would have caught.
        private static string? StringMemberChecked(
            JObject obj, string name, string jsonPath, out Diagnostic? error)
        {
            error = null;
            if (obj[name] is not { } token)
            {
                return null;
            }
            if (token is JValue { Type: JTokenType.String } value)
            {
                return (string?)value.Value;
            }
            error = ErrorCodes.Create(
                ErrorCodes.OW_TYPE_003,
                $"Ref '{name}' is not a JSON string.",
                jsonPath: Member(jsonPath, name));
            return null;
        }

        private static string Member(string path, string name) =>
            path == "$" ? "$." + name : path + "." + name;

        private static Result<T> Fail<T>(string code, string message, string jsonPath) =>
            Result<T>.Failure(ErrorCodes.Create(code, message, jsonPath: jsonPath));
    }
}
