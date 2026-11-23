#nullable enable

using System;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpWeave.Viewer
{
    /// <summary>
    /// The click behaviour of a SPEC §H5 Runs-tab change row: ping the
    /// changed asset in the Project window, select the changed GameObject
    /// in the Hierarchy. Resolution follows the target's durable §F5
    /// identifiers first — the <c>globalId</c> of a scene object, the
    /// <c>guid</c> of an asset — with the recorded path as the fallback,
    /// because the identifiers name the object the change touched while a
    /// reused path does not.
    /// </summary>
    internal static class RunChangeActivation
    {
        /// <summary>
        /// The Unity object <paramref name="target"/> resolves to right now,
        /// or null when none of its identifiers or its path still finds one —
        /// a deleted GameObject or a moved asset is then reported by the
        /// caller instead of selecting the wrong thing.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="target"/> is null.</exception>
        internal static UnityEngine.Object? Resolve(ChangeTarget target)
        {
            if (target is null)
            {
                throw new ArgumentNullException(nameof(target));
            }
            // The durable identifiers first: the globalId of a scene object
            // and the guid of an asset name the object itself, not a slot a
            // later create could have reused.
            if (target.GlobalId is { } globalId &&
                GlobalObjectId.TryParse(globalId, out var id))
            {
                var resolved = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id);
                if (resolved is not null)
                {
                    return resolved;
                }
            }
            var asset = ResolveAsset(target);
            if (asset is not null)
            {
                return asset;
            }
            return FindByHierarchyPath(target.Path);
        }

        /// <summary>
        /// Pings <paramref name="target"/>'s resolved object (an asset in the
        /// Project window) or selects it (a GameObject in the Hierarchy) —
        /// the SPEC §H5 click contract. Returns the resolved object, or null
        /// when nothing still resolves the target.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="target"/> is null.</exception>
        internal static UnityEngine.Object? Activate(ChangeTarget target)
        {
            var resolved = Resolve(target);
            if (resolved is null)
            {
                return null;
            }
            Selection.activeObject = resolved;
            EditorGUIUtility.PingObject(resolved);
            return resolved;
        }

        private static UnityEngine.Object? ResolveAsset(ChangeTarget target)
        {
            var path = target.Guid is { } guid && guid.Length != 0
                ? AssetDatabase.GUIDToAssetPath(guid)
                : null;
            if (string.IsNullOrEmpty(path))
            {
                path = target.Path;
            }
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            return AssetDatabase.LoadMainAssetAtPath(path);
        }

        // The scene-object fallback when no globalId resolves: walk the
        // §G2 HierarchyPath segments ("Root/Child[n]") through the loaded
        // scenes and take the first full match. The path carries no scene
        // identity, so every loaded scene is tried in order.
        private static GameObject? FindByHierarchyPath(string path)
        {
            if (!RefHierarchyPath.TryParse(path, out var segments) || segments.Length == 0)
            {
                return null;
            }
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var found = FindInScene(SceneManager.GetSceneAt(i), segments);
                if (found is not null)
                {
                    return found;
                }
            }
            return null;
        }

        private static GameObject? FindInScene(Scene scene, RefHierarchyPath.Segment[] segments)
        {
            var candidates = scene.GetRootGameObjects();
            GameObject? current = null;
            for (var i = 0; i < segments.Length; i++)
            {
                current = SelectSibling(candidates, segments[i]);
                if (current is null)
                {
                    return null;
                }
                var children = current.transform;
                var next = new GameObject[children.childCount];
                for (var c = 0; c < children.childCount; c++)
                {
                    next[c] = children.GetChild(c).gameObject;
                }
                candidates = next;
            }
            return current;
        }

        // The n-th same-name candidate when the segment carries the §G2
        // "[n]" sibling index, the first (and grammar-wise only) name match
        // otherwise.
        private static GameObject? SelectSibling(
            GameObject[] candidates, RefHierarchyPath.Segment segment)
        {
            var sameName = 0;
            foreach (var candidate in candidates)
            {
                if (!string.Equals(candidate.name, segment.Name, StringComparison.Ordinal))
                {
                    continue;
                }
                if (segment.Index < 0 || sameName == segment.Index)
                {
                    return candidate;
                }
                sameName++;
            }
            return null;
        }
    }
}
