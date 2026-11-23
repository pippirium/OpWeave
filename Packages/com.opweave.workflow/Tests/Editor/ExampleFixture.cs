#nullable enable

// T-W3.1-03 [Edit] test fixture: materializes the project fixture one
// OperationExample.FixtureManifest declares — the files/prefabs vocabulary
// the standard examples publish (SPEC §C1 fixes only that the manifest
// describes the fixture) — so an example's complete WF validates against
// assets the project really holds, and restores the project on Dispose.
// Internal to this test assembly: SPEC §M.6 fixes the OpWeave.Testing
// surface, and fixture application is not part of it.
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// Materializes the project fixture one
    /// <see cref="OperationExample.FixtureManifest"/> declares, so the
    /// example's complete WF can be validated against the assets it names.
    /// Everything <see cref="Apply"/> creates — files, Prefab assets and
    /// the folders holding them — is imported synchronously and deleted by
    /// <see cref="Dispose"/>; an asset already present at a declared path is
    /// taken as the applied fixture and is never removed.
    /// </summary>
    /// <remarks>
    /// The manifest vocabulary is the local, documented shape the standard
    /// examples publish (SPEC §C1 fixes only that the manifest describes the
    /// fixture): a <c>files</c> array of <c>{"path", "text"}</c> entries
    /// written verbatim, and a <c>prefabs</c> array of
    /// <c>{"path", "guid", "root"}</c> entries saved as one-root Prefabs
    /// whose <c>.meta</c> pins the declared guid — the identity a constant
    /// <c>unity.PrefabAssetRef</c> in the example workflow resolves by.
    /// Other members are left alone so an Operation may declare fixtures
    /// this fixture does not interpret; a malformed entry of a recognized
    /// kind is an argument error, never a silent skip.
    /// </remarks>
    internal sealed class ExampleFixture : IDisposable
    {
        // The fixture assets and folders Apply materialized: tracked so
        // Dispose restores the project to the state it found — an asset the
        // manifest's path already held is never recorded here, so a
        // pre-existing fixture is still standing after the fixture is gone.
        // Folders are appended parent-first and removed deepest-first.
        private readonly List<string> _fixtureAssets = new List<string>();
        private readonly List<string> _fixtureFolders = new List<string>();
        private bool _disposed;

        /// <summary>
        /// Materializes the fixture <paramref name="manifest"/> declares.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The fixture was disposed.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// The manifest is not a JSON object, or a <c>files</c>/<c>prefabs</c>
        /// entry lacks its required members or names a path outside
        /// <c>Assets/</c>.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// A fixture asset or folder could not be created, or an existing
        /// asset at a declared Prefab path does not carry the pinned guid.
        /// </exception>
        internal void Apply(JsonValue manifest)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ExampleFixture));
            }
            if (manifest is null)
            {
                throw new ArgumentNullException(nameof(manifest));
            }
            if (manifest.ToToken() is not JObject obj)
            {
                throw new ArgumentException(
                    "A fixture manifest must be a JSON object.", nameof(manifest));
            }
            ApplyManifestFiles(obj);
            ApplyManifestPrefabs(obj);
        }

        /// <summary>
        /// Deletes every asset and folder <see cref="Apply"/> created and
        /// refreshes the asset database, so the project after
        /// <see cref="Dispose"/> is the project before it. Idempotent, and
        /// safe inside a <c>using</c>.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            DeleteFixtureAssets();
            AssetDatabase.Refresh();
        }

        // The one-root Prefab .meta a prefabs entry is imported under: the
        // same minimal PrefabImporter text the §G6 fixture fixtures author,
        // with the manifest's pinned guid in the guid field — a public API
        // cannot assign an asset's guid, so the .meta carries it, exactly as
        // a checked-in fixture's would.
        private static string PrefabMeta(string guid) =>
            "fileFormatVersion: 2\n" +
            "guid: " + guid + "\n" +
            "PrefabImporter:\n" +
            "  externalObjects: {}\n" +
            "  userData: \n" +
            "  assetBundleName: \n" +
            "  assetBundleVariant: \n";

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string Disk(string assetPath) =>
            Path.Combine(
                ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));

        // A fixture path must name an asset inside the project — an
        // AssetDatabase-relative 'Assets/...' spelling, never a parent
        // traversal or the root itself.
        private static string CheckFixturePath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) ||
                !assetPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                assetPath.IndexOf("..", StringComparison.Ordinal) >= 0 ||
                assetPath.Length <= "Assets/".Length)
            {
                throw new ArgumentException(
                    "A fixture path must name an asset under 'Assets/'.");
            }
            return assetPath;
        }

        private void ApplyManifestFiles(JObject manifest)
        {
            if (manifest["files"] is not JArray files)
            {
                return;
            }
            for (var i = 0; i < files.Count; i++)
            {
                if (files[i] is not JObject file ||
                    file["path"] is not JValue { Type: JTokenType.String } path ||
                    file["text"] is not JValue { Type: JTokenType.String } text)
                {
                    throw new ArgumentException(
                        "A fixture 'files' entry must carry 'path' and 'text' strings.",
                        nameof(manifest));
                }
                var assetPath = CheckFixturePath((string)path.Value!);
                // An asset already present at the declared path is the
                // applied fixture: writing it again would leave content
                // Dispose cannot restore — the fixture touches only what
                // it creates.
                if (File.Exists(Disk(assetPath)))
                {
                    continue;
                }
                EnsureAssetFolders(assetPath);
                File.WriteAllText(Disk(assetPath), (string)text.Value!);
                AssetDatabase.ImportAsset(
                    assetPath, ImportAssetOptions.ForceSynchronousImport);
                _fixtureAssets.Add(assetPath);
            }
        }

        private void ApplyManifestPrefabs(JObject manifest)
        {
            if (manifest["prefabs"] is not JArray prefabs)
            {
                return;
            }
            for (var i = 0; i < prefabs.Count; i++)
            {
                if (prefabs[i] is not JObject prefab ||
                    prefab["path"] is not JValue { Type: JTokenType.String } path ||
                    prefab["guid"] is not JValue { Type: JTokenType.String } guid ||
                    prefab["root"] is not JValue { Type: JTokenType.String } root)
                {
                    throw new ArgumentException(
                        "A fixture 'prefabs' entry must carry 'path', 'guid' and 'root' strings.",
                        nameof(manifest));
                }
                var assetPath = CheckFixturePath((string)path.Value!);
                var pinnedGuid = (string)guid.Value!;
                if (AssetDatabase.LoadAssetAtPath<GameObject>(assetPath) is not null)
                {
                    // The fixture is already applied; the identity it was
                    // declared under must agree — a same-path asset under a
                    // different guid is a foreign asset, never the fixture.
                    if (!string.Equals(
                            AssetDatabase.AssetPathToGUID(assetPath), pinnedGuid,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"The asset already at '{assetPath}' does not carry the fixture's pinned guid.");
                    }
                    continue;
                }
                EnsureAssetFolders(assetPath);
                var gameObject = new GameObject((string)root.Value!);
                try
                {
                    if (PrefabUtility.SaveAsPrefabAsset(gameObject, assetPath) is null)
                    {
                        throw new InvalidOperationException(
                            $"Could not save the fixture Prefab at '{assetPath}'.");
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(gameObject);
                }
                File.WriteAllText(Disk(assetPath) + ".meta", PrefabMeta(pinnedGuid));
                AssetDatabase.ImportAsset(
                    assetPath, ImportAssetOptions.ForceSynchronousImport);
                if (!string.Equals(
                        AssetDatabase.AssetPathToGUID(assetPath), pinnedGuid,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"The fixture Prefab at '{assetPath}' did not import with its pinned guid.");
                }
                _fixtureAssets.Add(assetPath);
            }
        }

        // Every folder level a fixture path needs, parent first — created
        // through the asset database so Unity imports each one, and recorded
        // so Dispose can remove the empty chain again. 'Assets' itself is
        // the project's own folder and is never tracked.
        private void EnsureAssetFolders(string assetPath)
        {
            var lastSlash = assetPath.LastIndexOf('/');
            if (lastSlash <= "Assets".Length)
            {
                return;
            }
            var segments = assetPath.Substring(0, lastSlash).Split('/');
            var current = segments[0];
            for (var i = 1; i < segments.Length; i++)
            {
                var next = current + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    if (string.IsNullOrEmpty(
                            AssetDatabase.CreateFolder(current, segments[i])))
                    {
                        throw new InvalidOperationException(
                            $"Could not create the fixture folder '{next}'.");
                    }
                    _fixtureFolders.Add(next);
                }
                current = next;
            }
        }

        // The assets and folders Apply created, removed in the order that
        // keeps the project identical to the state the fixture found:
        // assets first, then the tracked folders deepest-first while each
        // still holds only .meta files — a folder that gained foreign
        // content stays.
        private void DeleteFixtureAssets()
        {
            for (var i = 0; i < _fixtureAssets.Count; i++)
            {
                var path = _fixtureAssets[i];
                if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
                {
                    AssetDatabase.DeleteAsset(path);
                }
                else
                {
                    var disk = Disk(path);
                    if (File.Exists(disk))
                    {
                        File.Delete(disk);
                    }
                    if (File.Exists(disk + ".meta"))
                    {
                        File.Delete(disk + ".meta");
                    }
                }
            }
            _fixtureAssets.Clear();
            for (var i = _fixtureFolders.Count - 1; i >= 0; i--)
            {
                var folder = _fixtureFolders[i];
                if (AssetDatabase.IsValidFolder(folder))
                {
                    if (IsEmptyFolder(folder))
                    {
                        AssetDatabase.DeleteAsset(folder);
                    }
                }
                else
                {
                    var disk = Disk(folder);
                    if (Directory.Exists(disk) && IsEmptyFolder(disk))
                    {
                        Directory.Delete(disk, recursive: true);
                        if (File.Exists(disk + ".meta"))
                        {
                            File.Delete(disk + ".meta");
                        }
                    }
                }
            }
            _fixtureFolders.Clear();
        }

        // Only a folder the fixture created itself, and only while nothing
        // but meta files is left in it, is removed — a folder someone else
        // reused or filled is never touched.
        private static bool IsEmptyFolder(string folder)
        {
            var entries = Directory.GetFileSystemEntries(
                folder, "*", SearchOption.AllDirectories);
            for (var i = 0; i < entries.Length; i++)
            {
                if (!entries[i].EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
