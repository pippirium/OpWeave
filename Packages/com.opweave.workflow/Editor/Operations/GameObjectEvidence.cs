#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpWeave.Operations
{
    /// <summary>
    /// The shared §F5 evidence helpers of the GameObject Operations: the
    /// snapshot shapes the Reconcilable <c>unity.gameobject.create</c> and
    /// <c>unity.gameobject.delete</c> write at Preflight and read back at
    /// Reconcile — a scene key, a parent GameObjectRef (or JSON null for the
    /// scene's root list), the ordered child entries (handle · globalId ·
    /// name) and the Editor session id of SPEC §F5 common rule 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The snapshot JSON is the implementation detail §F5 leaves to this
    /// ticket; the evidence it carries is the normative §F5 table row.
    /// Session handles (InstanceID below 6000.5, EntityId above it — L-04)
    /// survive a domain reload but not an Editor restart, which is exactly
    /// what the "same session" condition needs: after a reload the recorded
    /// handles still name the same objects, while a different
    /// <see cref="EditorSessionId"/> already fails the verdict closed.
    /// </para>
    /// </remarks>
    internal static class GameObjectEvidence
    {
        /// <summary>The scene identity recorded in a snapshot: handle for the
        /// same-session lookup, path and guid for the §G2 SceneRef keys.</summary>
        internal static JObject SceneKey(Scene scene) =>
            new JObject
            {
                ["kind"] = UnityShapeTypes.SceneKind,
#if UNITY_6000_5_OR_NEWER
                // Scene.handle is a SceneHandle on 6000.5+ (L-04). Its
                // GetRawData() ulong is stored bit-exact in the Int64 the
                // JSON integer carries: the unchecked reinterpret keeps the
                // member a JSON integer (never a string) and reads back
                // identically in ResolveScene — no truncation and no wrap
                // because Int64 and UInt64 share the same 64 bits.
                ["handle"] = unchecked((long)scene.handle.GetRawData()),
#else
                ["handle"] = scene.handle,
#endif
                ["path"] = scene.path,
                ["guid"] = scene.path.Length == 0
                    ? string.Empty
                    : UnityEditor.AssetDatabase.AssetPathToGUID(scene.path),
            };

        /// <summary>The full GameObjectRef JSON of <paramref name="go"/> —
        /// every key the object can hold (SPEC §G2).</summary>
        internal static JObject RefOf(GameObject go) =>
            (JObject)new RefResolver().ToRef(go, "unity.GameObjectRef").ToToken();

        /// <summary>One ordered child entry: the session handle, the globalId
        /// where the object's scene gives it a stable one (never on an unsaved
        /// scene, §G2) and the name.</summary>
        internal static JObject ChildEntry(GameObject child)
        {
            var entry = new JObject
            {
                ["handle"] = SessionHandle.Write(child),
                ["name"] = child.name,
            };
            var globalId = GlobalObjectId.GetGlobalObjectIdSlow(child).ToString();
            if (GlobalObjectId.TryParse(globalId, out _))
            {
                entry["globalId"] = globalId;
            }
            return entry;
        }

        /// <summary>The ordered children of a parent transform.</summary>
        internal static JArray ChildrenOf(Transform parent)
        {
            var children = new JArray();
            for (var i = 0; i < parent.childCount; i++)
            {
                children.Add(ChildEntry(parent.GetChild(i).gameObject));
            }
            return children;
        }

        /// <summary>The ordered root list of a scene — the "children" of a
        /// GameObject-less parent (SPEC §F5: 未指定ならルート).</summary>
        internal static JArray ChildrenOf(Scene scene)
        {
            var children = new JArray();
            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                children.Add(ChildEntry(roots[i]));
            }
            return children;
        }

        /// <summary>The live children the snapshot's parent row addresses:
        /// the transform's children, or the scene's roots when the recorded
        /// parent is JSON null.</summary>
        internal static IReadOnlyList<GameObject> LiveChildren(
            Transform? parent, Scene scene)
        {
            var children = new List<GameObject>();
            if (parent != null)
            {
                for (var i = 0; i < parent.childCount; i++)
                {
                    children.Add(parent.GetChild(i).gameObject);
                }
            }
            else
            {
                children.AddRange(scene.GetRootGameObjects());
            }
            return children;
        }

        /// <summary>
        /// Resolves the snapshot's scene key back to the loaded scene: the
        /// recorded handle first (same session, across a domain reload), then
        /// the §G2 SceneRef keys (guid → path → GetSceneByPath).
        /// </summary>
        internal static Scene? ResolveScene(JObject sceneKey)
        {
            if (sceneKey["handle"] is JValue { Type: JTokenType.Integer } handleToken)
            {
                // Unity 6000.0 has no SceneManager.GetSceneByHandle, so the
                // recorded handle is matched by walking the loaded scenes.
                // The session-boundary safety is unchanged: both Reconcile
                // implementations reject a different editorSessionId before
                // this lookup runs, so a handle is only ever consulted inside
                // the session that recorded it (SPEC §F5 common rule 3).
#if UNITY_6000_5_OR_NEWER
                // The recorded integer is the SceneHandle's raw ulong held
                // bit-exact in an Int64; a loaded scene matches when its
                // handle reads the same raw value the same way.
                var raw = unchecked((ulong)(long)handleToken.Value!);
#else
                var handle = (int)(long)handleToken.Value!;
#endif
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var candidate = SceneManager.GetSceneAt(i);
#if UNITY_6000_5_OR_NEWER
                    if (candidate.handle.GetRawData() == raw &&
#else
                    if (candidate.handle == handle &&
#endif
                        candidate.IsValid() && candidate.isLoaded)
                    {
                        return candidate;
                    }
                }
            }
            var sceneRef = new JObject
            {
                ["kind"] = UnityShapeTypes.SceneKind,
                ["path"] = sceneKey["path"] ?? JValue.CreateString(string.Empty),
                ["guid"] = sceneKey["guid"] ?? JValue.CreateString(string.Empty),
            };
            var resolved = new RefResolver().ResolveScene(JsonValue.From(sceneRef));
            return resolved.Ok ? resolved.Value : null;
        }

        /// <summary>
        /// Resolves a recorded parent GameObjectRef to the live GameObject,
        /// or <see langword="null"/> when no key still names it. A JSON null
        /// recorded parent answers <see langword="null"/> with
        /// <paramref name="isRoot"/> set — the scene's root list is the
        /// "parent" whose children the §F5 row compares.
        /// </summary>
        internal static GameObject? ResolveParent(JToken? parentToken, out bool isRoot)
        {
            isRoot = false;
            if (parentToken is null || parentToken.Type == JTokenType.Null)
            {
                isRoot = true;
                return null;
            }
            if (parentToken is not JObject parentRef)
            {
                return null;
            }
            var resolved = new RefResolver().Resolve<GameObject>(JsonValue.From(parentRef));
            return resolved.Ok ? resolved.Value : null;
        }

        /// <summary>The session handle key of a snapshot child entry, or
        /// <see langword="null"/> when the member is not a readable handle.</summary>
        internal static ulong? HandleOf(JToken? entry)
        {
            if (entry is not JObject obj || obj["handle"] is not { } token)
            {
                return null;
            }
            return SessionHandle.TryRead(token, out var raw) ? raw : (ulong?)null;
        }

        /// <summary>
        /// The session handle keys of a snapshot children array — the set a
        /// Reconcile subtracts from the live children to find what the step
        /// added, or to prove nothing changed.
        /// </summary>
        internal static HashSet<ulong> SnapshotHandles(JToken? childrenToken)
        {
            var handles = new HashSet<ulong>();
            if (childrenToken is JArray children)
            {
                foreach (var entry in children)
                {
                    if (HandleOf(entry) is { } raw)
                    {
                        handles.Add(raw);
                    }
                }
            }
            return handles;
        }

        /// <summary>
        /// Whether <paramref name="live"/> and the snapshot entry name the
        /// same child: identical session handle, identical globalId when both
        /// carry one, identical name.
        /// </summary>
        internal static bool SameChild(JToken? snapshotEntry, GameObject live)
        {
            if (snapshotEntry is not JObject entry || HandleOf(entry) is not { } raw)
            {
                return false;
            }
            if (SessionHandle.RawOf(live) != raw)
            {
                return false;
            }
            var recordedGlobalId = entry["globalId"] is JValue { Type: JTokenType.String } id
                ? (string?)id.Value
                : null;
            var liveGlobalId = GlobalObjectId.GetGlobalObjectIdSlow(live).ToString();
            if (recordedGlobalId is not null &&
                (!GlobalObjectId.TryParse(liveGlobalId, out _) ||
                    !string.Equals(recordedGlobalId, liveGlobalId, StringComparison.Ordinal)))
            {
                return false;
            }
            return string.Equals(
                entry["name"] is JValue { Type: JTokenType.String } name
                    ? (string?)name.Value
                    : null,
                live.name,
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Whether the live children equal the recorded list element for
        /// element, in order — the "list unchanged" half of NotApplied and
        /// the "list minus the target" half of Completed (§F5).
        /// </summary>
        internal static bool SameChildList(
            JToken? snapshotChildren, IReadOnlyList<GameObject> live)
        {
            if (snapshotChildren is not JArray recorded || recorded.Count != live.Count)
            {
                return false;
            }
            for (var i = 0; i < live.Count; i++)
            {
                if (!SameChild(recorded[i], live[i]))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Whether a live child already appears in the snapshot list — matched
        /// by session handle, the session-stable identity.
        /// </summary>
        internal static bool IsSnapshotChild(JToken? snapshotChildren, GameObject live)
        {
            if (snapshotChildren is JArray recorded)
            {
                foreach (var entry in recorded)
                {
                    if (HandleOf(entry) is { } raw && raw == SessionHandle.RawOf(live))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>The ordered component class names of a GameObject — the
        /// "primitive の構成" half of the create row's content check.</summary>
        internal static string[] ComponentNames(GameObject go)
        {
            var components = go.GetComponents<Component>();
            var names = new string[components.Length];
            for (var i = 0; i < components.Length; i++)
            {
                names[i] = components[i] == null ? "<missing>" : components[i].GetType().Name;
            }
            return names;
        }

        /// <summary>The component composition a freshly created object of
        /// <paramref name="primitive"/> carries — the expected content the
        /// §F5 row compares against. Transform leads because Unity keeps it
        /// at index 0 on every GameObject; the rest is a multiset — the
        /// Editor's component order after Transform is not contract (6000.0
        /// lists a fresh cube's BoxCollider before its MeshRenderer).</summary>
        internal static string[] ExpectedComponents(string primitive) => primitive switch
        {
            "none" => new[] { "Transform" },
            "cube" => new[] { "Transform", "MeshFilter", "MeshRenderer", "BoxCollider" },
            "sphere" => new[] { "Transform", "MeshFilter", "MeshRenderer", "SphereCollider" },
            "capsule" => new[] { "Transform", "MeshFilter", "MeshRenderer", "CapsuleCollider" },
            "cylinder" => new[] { "Transform", "MeshFilter", "MeshRenderer", "CapsuleCollider" },
            "plane" => new[] { "Transform", "MeshFilter", "MeshRenderer", "MeshCollider" },
            "quad" => new[] { "Transform", "MeshFilter", "MeshRenderer", "MeshCollider" },
            _ => Array.Empty<string>(),
        };

        /// <summary>Whether the live component list equals the expected one
        /// <b>and</b> the primitive's own content is provably present — the
        /// §F5 "primitive composition" content check of the create row's
        /// Completed verdict. Class names alone cannot distinguish a plane
        /// from a quad or a capsule from a cylinder (identical component
        /// lists), so a real primitive additionally carries its built-in
        /// mesh (Unity object identity with the mesh
        /// <c>Resources.GetBuiltinResource</c> resolves for the primitive —
        /// after a domain reload both sides are the same reloaded built-in
        /// object) and the collider type and configuration
        /// <c>GameObject.CreatePrimitive</c> produces. Anything the check
        /// cannot prove returns <see langword="false"/> with
        /// <paramref name="reason"/> naming the first failing sub-check —
        /// the caller fails closed to
        /// <see cref="ReconcileResult.Unknown"/>.</summary>
        internal static bool ComponentsMatch(
            GameObject go, string primitive, out string reason)
        {
            var expected = ExpectedComponents(primitive);
            var actual = ComponentNames(go);
            reason = "component list differs";
            if (expected.Length == 0 || actual.Length != expected.Length)
            {
                return false;
            }
            // Transform still leads — Unity anchors it at index 0 — and the
            // rest of the composition is compared as a multiset of exact
            // component class names: equal cardinality plus equal sorted
            // names rejects an extra, missing or altered component exactly
            // as the ordered compare did, without depending on an Editor
            // component order that is not contract.
            if (!string.Equals(actual[0], expected[0], StringComparison.Ordinal))
            {
                return false;
            }
            var actualRest = (string[])actual.Clone();
            var expectedRest = (string[])expected.Clone();
            Array.Sort(actualRest, 1, actualRest.Length - 1, StringComparer.Ordinal);
            Array.Sort(expectedRest, 1, expectedRest.Length - 1, StringComparer.Ordinal);
            for (var i = 1; i < expectedRest.Length; i++)
            {
                if (!string.Equals(actualRest[i], expectedRest[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return PrimitiveContentMatches(go, primitive, out reason);
        }

        // The primitive's own content: the MeshFilter's mesh is the built-in
        // primitive mesh itself — Unity object identity with the resource
        // GetBuiltinResource resolves for the primitive's "<Name>.fbx" (a
        // user asset or a scene-local mesh with the same name proves
        // nothing; after a domain reload both sides name the same reloaded
        // built-in object), and the collider carries the configuration
        // CreatePrimitive assigns. The mesh alone separates plane/quad and
        // capsule/cylinder, whose component lists and collider
        // configurations are otherwise identical. An Editor that cannot
        // produce the reference mesh fails closed too.
        private static bool PrimitiveContentMatches(
            GameObject go, string primitive, out string reason)
        {
            var meshName = ExpectedMeshName(primitive);
            if (meshName is null)
            {
                // "none": the component list already proves composition.
                reason = string.Empty;
                return true;
            }
            var builtin = Resources.GetBuiltinResource<Mesh>(meshName + ".fbx");
            if (builtin == null)
            {
                reason = "the built-in primitive mesh could not be loaded";
                return false;
            }
            var filter = go.GetComponent<MeshFilter>();
            var mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null || mesh != builtin)
            {
                reason = "mesh does not match the built-in primitive";
                return false;
            }
            switch (primitive)
            {
                case "cube":
                {
                    var box = go.GetComponent<BoxCollider>();
                    if (box == null ||
                        !SameVector(box.center, Vector3.zero) ||
                        !SameVector(box.size, Vector3.one))
                    {
                        reason = "collider configuration differs";
                        return false;
                    }
                    break;
                }
                case "sphere":
                {
                    var sphere = go.GetComponent<SphereCollider>();
                    if (sphere == null ||
                        !SameVector(sphere.center, Vector3.zero) ||
                        !Mathf.Approximately(sphere.radius, 0.5f))
                    {
                        reason = "collider configuration differs";
                        return false;
                    }
                    break;
                }
                case "capsule":
                case "cylinder":
                {
                    var capsule = go.GetComponent<CapsuleCollider>();
                    if (capsule == null ||
                        !SameVector(capsule.center, Vector3.zero) ||
                        !Mathf.Approximately(capsule.radius, 0.5f) ||
                        !Mathf.Approximately(capsule.height, 2f) ||
                        capsule.direction != 1)
                    {
                        reason = "collider configuration differs";
                        return false;
                    }
                    break;
                }
                case "plane":
                case "quad":
                {
                    var meshCollider = go.GetComponent<MeshCollider>();
                    if (meshCollider == null || meshCollider.sharedMesh != builtin)
                    {
                        reason = "collider configuration differs";
                        return false;
                    }
                    break;
                }
                default:
                    reason = "the primitive is not recognized";
                    return false;
            }
            reason = string.Empty;
            return true;
        }

        private static string? ExpectedMeshName(string primitive) => primitive switch
        {
            "cube" => "Cube",
            "sphere" => "Sphere",
            "capsule" => "Capsule",
            "cylinder" => "Cylinder",
            "plane" => "Plane",
            "quad" => "Quad",
            _ => null,
        };

        /// <summary>Whether two vectors agree within Unity's float
        /// tolerance — every transform and collider number Unity stores is a
        /// float, so the content check compares components with
        /// <see cref="Mathf.Approximately"/> instead of exact equality.</summary>
        internal static bool SameVector(Vector3 expected, Vector3 actual) =>
            Mathf.Approximately(expected.x, actual.x) &&
            Mathf.Approximately(expected.y, actual.y) &&
            Mathf.Approximately(expected.z, actual.z);

        /// <summary>Whether <paramref name="value"/> is a finite vector —
        /// non-finite components never describe a created transform.</summary>
        internal static bool IsFinite(Vector3 value) =>
            !(float.IsNaN(value.x) || float.IsNaN(value.y) || float.IsNaN(value.z) ||
                float.IsInfinity(value.x) || float.IsInfinity(value.y) || float.IsInfinity(value.z));

        /// <summary>The fixed English <see cref="ReconcileResult.Unknown"/> —
        /// §F5's fail-closed verdict when the evidence cannot prove
        /// Completed or NotApplied.</summary>
        internal static ReconcileResult Unknown(string reason) =>
            new ReconcileResult.Unknown(reason);
    }
}
