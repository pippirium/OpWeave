#nullable enable

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace OpWeave.Testing
{
    /// <summary>
    /// A comparable picture of the world a contract assertion runs in
    /// (SPEC §M.6 item 2): every project asset — path, GUID and content — and
    /// every GameObject of every loaded scene — hierarchy path, name and
    /// serialized component content. Two captures diff into the observed
    /// changes a run made, which is what
    /// <see cref="OperationContract.AssertDeclaredEffectsOnly"/> judges the
    /// declared SPEC §C4 vocabulary against and what
    /// <see cref="OperationContract.AssertIdempotency"/> and
    /// <see cref="OperationContract.AssertUndo"/> compare for equality.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The snapshot observes the world directly instead of trusting the
    /// Operation's SPEC §F7 records: a contract test's whole point is that a
    /// lying Descriptor must be caught even when the Operation never records
    /// the change it made — the same reason SPEC §F7 automatically binds
    /// importer-reported asset changes to the step.
    /// </para>
    /// <para>
    /// Assets are keyed by path and hashed over file content, so a re-save
    /// with different bytes is a modification and an unchanged file is not;
    /// a move is recognized by GUID surviving at a new path — and a move
    /// whose bytes changed in transit reads as a modification at its
    /// destination too. GameObjects are
    /// keyed by their §G2 HierarchyPath inside their scene and hashed over
    /// the components' serialized content, so creating, deleting, renaming
    /// or editing an object is each a visible change.
    /// </para>
    /// </remarks>
    internal sealed class HarnessWorldSnapshot
    {
        /// <summary>
        /// One observed difference between two captures, in the
        /// (<see cref="ChangeAction"/>, target kind) shape the SPEC §C4 effect
        /// mapping consumes. <see cref="Path"/> is the asset path or the
        /// scene-qualified HierarchyPath the difference applies to.
        /// </summary>
        internal readonly struct ObservedChange
        {
            internal ObservedChange(ChangeAction action, string kind, string path)
            {
                Action = action;
                Kind = kind;
                Path = path;
            }

            internal ChangeAction Action { get; }

            internal string Kind { get; }

            internal string Path { get; }
        }

        private readonly Dictionary<string, (string Guid, string Content)>
            _assets = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        private readonly Dictionary<string, string>
            _objects = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Captures the world now: refreshes the AssetDatabase first so files
        /// an Operation wrote outside the database are counted too.
        /// </summary>
        internal static HarnessWorldSnapshot Capture()
        {
            AssetDatabase.Refresh();
            var snapshot = new HarnessWorldSnapshot();
            snapshot.CaptureAssets();
            snapshot.CaptureScenes();
            return snapshot;
        }

        /// <summary>
        /// The observed changes turning this snapshot into
        /// <paramref name="later"/>, in a deterministic order (assets by path,
        /// then objects by key).
        /// </summary>
        internal List<ObservedChange> ChangesTo(HarnessWorldSnapshot later)
        {
            if (later is null)
            {
                throw new ArgumentNullException(nameof(later));
            }
            var changes = new List<ObservedChange>();
            DiffAssets(changes, later);
            DiffObjects(changes, later);
            return changes;
        }

        /// <summary>True when <paramref name="other"/> observes no difference.</summary>
        internal bool SameAs(HarnessWorldSnapshot other) =>
            other is not null && ChangesTo(other).Count == 0;

        // ── assets ───────────────────────────────────────────────────────

        private void CaptureAssets()
        {
            foreach (var guid in AssetDatabase.FindAssets("", new[] { "Assets" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }
                _assets[path] = (guid, AssetSignature(path));
            }
        }

        // The content half of an asset entry: the file's bytes hashed, or the
        // folder's marker — plus the .meta bytes, which carry the importer
        // settings a modification may change without touching the file.
        private static string AssetSignature(string assetPath)
        {
            var builder = new StringBuilder();
            var fullPath = FullPath(assetPath);
            if (AssetDatabase.IsValidFolder(assetPath))
            {
                builder.Append("folder");
            }
            else if (System.IO.File.Exists(fullPath))
            {
                builder.Append("file:").Append(HashFile(fullPath));
            }
            else
            {
                builder.Append("other");
            }
            var metaPath = fullPath + ".meta";
            if (System.IO.File.Exists(metaPath))
            {
                builder.Append(";meta:").Append(HashFile(metaPath));
            }
            return builder.ToString();
        }

        private void DiffAssets(
            List<ObservedChange> changes, HarnessWorldSnapshot later)
        {
            var laterByGuid = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in later._assets)
            {
                laterByGuid[pair.Value.Guid] = pair.Key;
            }
            // An asset whose GUID survives at another path moved there
            // (SPEC §C4 keeps asset.move separate from asset.create): its
            // destination is reported once as Moved — it is not also a
            // creation, even though the path was absent from the earlier
            // capture. The same applies when the move landed on a path
            // another asset still occupied.
            var movedDestinations = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in _assets)
            {
                if (later._assets.TryGetValue(pair.Key, out var same) &&
                    string.Equals(same.Guid, pair.Value.Guid, StringComparison.Ordinal))
                {
                    continue;
                }
                if (laterByGuid.TryGetValue(pair.Value.Guid, out var destination))
                {
                    movedDestinations.Add(destination);
                }
            }
            foreach (var pair in later._assets)
            {
                if (!_assets.TryGetValue(pair.Key, out var before))
                {
                    if (!movedDestinations.Contains(pair.Key))
                    {
                        changes.Add(new ObservedChange(
                            ChangeAction.Created, "asset", pair.Key));
                    }
                }
                else if (!string.Equals(
                    before.Guid, pair.Value.Guid, StringComparison.Ordinal))
                {
                    // A recreated asset at the same path is a delete plus a
                    // create, not a modification — unless the new asset is
                    // the destination of a move, which Moved already says.
                    changes.Add(new ObservedChange(ChangeAction.Deleted, "asset", pair.Key));
                    if (!movedDestinations.Contains(pair.Key))
                    {
                        changes.Add(new ObservedChange(
                            ChangeAction.Created, "asset", pair.Key));
                    }
                }
                else if (!string.Equals(
                    before.Content, pair.Value.Content, StringComparison.Ordinal))
                {
                    changes.Add(new ObservedChange(ChangeAction.Modified, "asset", pair.Key));
                }
            }
            foreach (var pair in _assets)
            {
                if (later._assets.TryGetValue(pair.Key, out var same) &&
                    string.Equals(same.Guid, pair.Value.Guid, StringComparison.Ordinal))
                {
                    continue;
                }
                if (laterByGuid.TryGetValue(pair.Value.Guid, out var destination))
                {
                    changes.Add(new ObservedChange(
                        ChangeAction.Moved, "asset", pair.Key + " -> " + destination));
                    // The move alone does not cover changed bytes: SPEC §C4
                    // keeps asset.move apart from asset.modify, so a
                    // surviving GUID whose content signature changed in
                    // transit is a modification at its destination too.
                    if (!string.Equals(
                        pair.Value.Content,
                        later._assets[destination].Content,
                        StringComparison.Ordinal))
                    {
                        changes.Add(new ObservedChange(
                            ChangeAction.Modified, "asset", destination));
                    }
                }
                else
                {
                    changes.Add(new ObservedChange(ChangeAction.Deleted, "asset", pair.Key));
                }
            }
        }

        // ── scenes ───────────────────────────────────────────────────────

        private void CaptureScenes()
        {
            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                {
                    continue;
                }
                foreach (var root in scene.GetRootGameObjects())
                {
                    CaptureObject(scene.path, root.transform);
                }
            }
        }

        private void CaptureObject(string scenePath, Transform transform)
        {
            var gameObject = transform.gameObject;
            _objects[ObjectKey(scenePath, gameObject)] = ObjectSignature(gameObject);
            for (var i = 0; i < transform.childCount; i++)
            {
                CaptureObject(scenePath, transform.GetChild(i));
            }
        }

        private static string ObjectKey(string scenePath, GameObject gameObject) =>
            scenePath + "|" + RefHierarchyPath.For(gameObject);

        // The object's full observable state: its active flag plus every
        // component's serialized content — which is exactly what the Undo
        // system restores, so it is the level AssertUndo compares at.
        private static string ObjectSignature(GameObject gameObject)
        {
            var builder = new StringBuilder();
            builder.Append(gameObject.name);
            builder.Append(gameObject.activeSelf ? "|active" : "|inactive");
            foreach (var component in gameObject.GetComponents<Component>())
            {
                builder.Append('\n');
                if (component is null)
                {
                    builder.Append("<missing>");
                    continue;
                }
                builder.Append(component.GetType().FullName);
                builder.Append(':');
                try
                {
                    builder.Append(EditorJsonUtility.ToJson(component));
                }
                catch (Exception)
                {
                    // A component the serializer cannot read still counts —
                    // its presence is part of the object's signature.
                    builder.Append("<unserializable>");
                }
            }
            return builder.ToString();
        }

        private void DiffObjects(
            List<ObservedChange> changes, HarnessWorldSnapshot later)
        {
            foreach (var pair in later._objects)
            {
                if (!_objects.TryGetValue(pair.Key, out var before))
                {
                    changes.Add(new ObservedChange(
                        ChangeAction.Created, "gameObject", pair.Key));
                }
                else if (!string.Equals(before, pair.Value, StringComparison.Ordinal))
                {
                    changes.Add(new ObservedChange(
                        ChangeAction.Modified, "gameObject", pair.Key));
                }
            }
            foreach (var pair in _objects)
            {
                if (!later._objects.ContainsKey(pair.Key))
                {
                    changes.Add(new ObservedChange(
                        ChangeAction.Deleted, "gameObject", pair.Key));
                }
            }
        }

        // ── shared ───────────────────────────────────────────────────────

        private static string FullPath(string assetPath) =>
            System.IO.Path.Combine(
                System.IO.Directory.GetCurrentDirectory(), assetPath);

        private static string HashFile(string fullPath)
        {
            try
            {
                using var stream = System.IO.File.OpenRead(fullPath);
                using var sha = SHA256.Create();
                var hash = sha.ComputeHash(stream);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                {
                    builder.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                }
                return builder.ToString();
            }
            catch (Exception)
            {
                return "<unreadable>";
            }
        }
    }
}
