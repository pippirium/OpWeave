#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;

namespace OpWeave.Operations
{
    /// <summary>
    /// The on-disk state of one project asset path, shared by the §F5
    /// Reconcilable Asset Operations (SPEC §G7): whether anything occupies a
    /// target path, where a project asset path lives on disk, which folder
    /// would own it, what content it carries and which assets sit beside it —
    /// the questions Preflight asks before a side effect and Reconcile asks
    /// when classifying the outcome.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The helpers mirror the private ones
    /// <c>CreateScriptFromTemplateOperation</c> carries for the same contract
    /// (T-W4.2-01): an occupied path is never just "the asset database knows
    /// it" — a file, a directory, a leftover orphan <c>.meta</c> the refresh
    /// has not cleaned up, or an asset the database still holds all count,
    /// because a create step never writes over an existing path. The
    /// existence question needs
    /// <see cref="AssetPathToGUIDOptions.OnlyExistingAssets"/>: the default
    /// overload also answers for deleted assets whose path→GUID mapping the
    /// database still remembers, which would call an empty path occupied.
    /// </para>
    /// </remarks>
    internal static class AssetArtifact
    {
        /// <summary>
        /// Whether anything occupies <paramref name="assetPath"/>: a file, a
        /// directory, an orphan <c>.meta</c>, or an existing asset database
        /// entry.
        /// </summary>
        internal static bool Occupied(string physical, string assetPath) =>
            File.Exists(physical) ||
            Directory.Exists(physical) ||
            File.Exists(physical + ".meta") ||
            AssetDatabase.AssetPathToGUID(
                assetPath, AssetPathToGUIDOptions.OnlyExistingAssets).Length > 0;

        /// <summary>
        /// The path <paramref name="guid"/> currently resolves to as a live
        /// asset, or <see langword="null"/> when it does not.
        /// <see cref="AssetDatabase.GUIDToAssetPath"/> can keep answering
        /// with a deleted asset's former path, so the mapping counts only
        /// when the path holds a file or directory on disk, the database
        /// still imports it under the same guid and its main asset loads —
        /// a stale mapping or a different artifact at the same path is not
        /// the recorded target.
        /// </summary>
        internal static string? LiveAssetPath(string guid)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.Length == 0)
            {
                return null;
            }
            var physical = ToPhysicalPath(path);
            if (physical is null || (!File.Exists(physical) && !Directory.Exists(physical)))
            {
                return null;
            }
            if (!string.Equals(
                AssetDatabase.AssetPathToGUID(
                    path, AssetPathToGUIDOptions.OnlyExistingAssets),
                guid,
                StringComparison.Ordinal))
            {
                return null;
            }
            return Directory.Exists(physical)
                ? (AssetDatabase.IsValidFolder(path) ? path : null)
                : (AssetDatabase.LoadMainAssetAtPath(path) is not null ? path : null);
        }

        /// <summary>
        /// The physical location of a project asset path — the same mapping
        /// the requirement evaluator applies: <c>Packages/</c> resolves
        /// through the owning package (a local or embedded package may live
        /// outside the project root), anything else resolves under the
        /// project root. <see langword="null"/> when the path cannot be
        /// resolved inside the project.
        /// </summary>
        internal static string? ToPhysicalPath(string assetPath)
        {
            if (assetPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
                var prefix = info?.assetPath is { } packageRoot
                    ? packageRoot + "/"
                    : null;
                if (prefix is null ||
                    !assetPath.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return null;
                }
                return Path.GetFullPath(Path.Combine(
                    info!.resolvedPath, assetPath.Substring(prefix.Length)));
            }
            var full = Path.GetFullPath(assetPath);
            var projectRoot =
                Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "..")) +
                Path.DirectorySeparatorChar;
            return full.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase)
                ? full
                : null;
        }

        /// <summary>
        /// The folder an asset path sits in — everything before the last
        /// <c>/</c>; <see langword="null"/> for a path without one.
        /// </summary>
        internal static string? ParentFolder(string assetPath)
        {
            var slash = assetPath.LastIndexOf('/');
            return slash <= 0 ? null : assetPath.Substring(0, slash);
        }

        /// <summary>
        /// The file name of an asset path without its extension — the name
        /// the importer gives the main asset at that path.
        /// </summary>
        internal static string FileBaseName(string assetPath)
        {
            var name = Path.GetFileName(assetPath);
            var dot = name.LastIndexOf('.');
            return dot <= 0 ? name : name.Substring(0, dot);
        }

        /// <summary>
        /// The §F5 content hash of the asset file or folder at
        /// <paramref name="assetPath"/> — the identity the move, copy and
        /// delete evidence rows record before a side effect and re-verify
        /// after an interruption (SPEC §F5's "内容ハッシュ"). A file hashes
        /// its bytes plus its <c>.meta</c> text with the <c>guid:</c> line
        /// stripped, so a copy — whose <c>.meta</c> differs only in the
        /// fresh guid — hashes identically (the copy row's "内容ハッシュ
        /// （.meta の guid を除く）"). A folder hashes a canonical digest of
        /// its recursive listing: every descendant file's relative path and
        /// hash plus every descendant folder's normalized <c>.meta</c>.
        /// <see langword="null"/> when nothing exists at the path — no file,
        /// no directory and an unresolvable physical path — which the caller
        /// treats as evidence it could not collect.
        /// </summary>
        internal static string? ContentHash(string assetPath)
        {
            var physical = ToPhysicalPath(assetPath);
            if (physical is null)
            {
                return null;
            }
            if (File.Exists(physical))
            {
                return FileArtifactHash(physical);
            }
            return Directory.Exists(physical)
                ? DirectoryArtifactHash(physical)
                : null;
        }

        /// <summary>
        /// The neighbours of <paramref name="assetPath"/> inside its parent
        /// folder — the "other assets" evidence of the delete row's
        /// Completed condition ("他のアセットは消えていない"): every
        /// non-<c>.meta</c> file-system entry beside the target, mapped to
        /// its imported guid (<see cref="string.Empty"/> when the entry is
        /// not imported). <see langword="null"/> when the parent folder
        /// cannot be resolved or no longer exists.
        /// </summary>
        internal static SortedDictionary<string, string>? SiblingGuids(
            string assetPath)
        {
            var parent = ParentFolder(assetPath);
            var physical = parent is null ? null : ToPhysicalPath(parent);
            if (physical is null || !Directory.Exists(physical))
            {
                return null;
            }
            var targetName = Path.GetFileName(assetPath);
            var siblings = new SortedDictionary<string, string>(
                StringComparer.Ordinal);
            foreach (var entry in Directory.EnumerateFileSystemEntries(physical))
            {
                var name = Path.GetFileName(entry);
                if (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, targetName, StringComparison.Ordinal))
                {
                    continue;
                }
                siblings[name] = AssetDatabase.AssetPathToGUID(
                    parent! + "/" + name,
                    AssetPathToGUIDOptions.OnlyExistingAssets);
            }
            return siblings;
        }

        // The hash of one asset file: the file bytes and its normalized
        // .meta text digested together. A missing .meta contributes a fixed
        // marker so presence is part of the evidence.
        private static string FileArtifactHash(string physical)
        {
            string fileHash;
            using (var stream = File.OpenRead(physical))
            {
                fileHash = HashStream(stream);
            }
            var meta = physical + ".meta";
            var metaHash = File.Exists(meta)
                ? HashText(NormalizedMetaText(meta))
                : "-";
            return HashText("F:" + fileHash + ";M:" + metaHash);
        }

        // The hash of an asset folder: its normalized .meta plus a
        // canonical listing of every descendant entry — a file contributes
        // its relative path and artifact hash, a directory its relative
        // path and normalized .meta so an emptied folder still differs from
        // a never-populated one.
        private static string DirectoryArtifactHash(string physical)
        {
            var entries = new List<string>();
            CollectEntries(physical, physical, entries);
            entries.Sort(StringComparer.Ordinal);
            var meta = physical + ".meta";
            var selfHash = File.Exists(meta)
                ? HashText(NormalizedMetaText(meta))
                : "-";
            var builder = new StringBuilder("D:").Append(selfHash);
            foreach (var entry in entries)
            {
                builder.Append('\n').Append(entry);
            }
            return HashText(builder.ToString());
        }

        private static void CollectEntries(
            string root, string directory, List<string> entries)
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                entries.Add(Relative(root, file) + " F:" + FileArtifactHash(file));
            }
            foreach (var sub in Directory.EnumerateDirectories(directory))
            {
                var meta = sub + ".meta";
                var metaHash = File.Exists(meta)
                    ? HashText(NormalizedMetaText(meta))
                    : "-";
                entries.Add(Relative(root, sub) + " D:" + metaHash);
                CollectEntries(root, sub, entries);
            }
        }

        private static string Relative(string root, string path) =>
            path.Substring(root.Length).Replace('\\', '/').TrimStart('/');

        // The .meta text with the 'guid:' value stripped — the one member a
        // copy is allowed to differ in (SPEC §F5 unity.asset.copy row). All
        // other lines hash verbatim.
        private static string NormalizedMetaText(string metaPath)
        {
            var builder = new StringBuilder();
            foreach (var line in File.ReadLines(metaPath))
            {
                builder
                    .Append(line.StartsWith("guid:", StringComparison.Ordinal)
                        ? "guid:"
                        : line)
                    .Append('\n');
            }
            return builder.ToString();
        }

        private static string HashStream(Stream stream)
        {
            using var sha = SHA256.Create();
            return Hex(sha.ComputeHash(stream));
        }

        private static string HashText(string text)
        {
            using var sha = SHA256.Create();
            return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }

        private static string Hex(byte[] hash)
        {
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
            {
                builder.Append(b.ToString("x2"));
            }
            return builder.ToString();
        }
    }
}
