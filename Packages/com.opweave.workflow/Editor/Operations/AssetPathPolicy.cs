#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.PackageManager;

namespace OpWeave.Operations
{
    /// <summary>
    /// The SPEC §G7 item 1 asset path policy shared by the standard Asset
    /// Operations: the path is normalized to <c>/</c> separators, a path
    /// containing a <c>..</c> segment or spelled as a rooted, absolute,
    /// drive-letter or <c>~</c> path is refused with OW-PERM-002, a write
    /// whose destination lies in a read-only package is refused with
    /// OW-UNITY-010 (<c>Packages/</c> is readable only), and the roots a read
    /// may cover are the Run's allowed paths plus <c>Packages/</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Where the allowed-paths half of a write lives.</b> For a write the
    /// other half of SPEC §G7 item 1 — a path outside the settings
    /// <c>allowedPaths</c> (default <c>Assets/</c>) is OW-PERM-002 — is not
    /// decided here. It is the <c>path_writable</c> Requirement each write
    /// port of these Operations declares (SPEC §G1 item 4), evaluated by
    /// <c>EditorRequirementEvaluator</c> against the settings snapshot the
    /// Run's own acceptance validated (SPEC §H4.1, decision D-R6), which is
    /// also what makes the static D6 check of a constant path fire. Reading
    /// the settings file again here would let a file edited mid-Run loosen the
    /// contract the Plan was validated under, so this policy deliberately
    /// answers only what needs no settings: the path shape and the
    /// package-mutability half of "read-only". Both refusals keep the §G1
    /// precedence the evaluator uses: the path shape first (OW-PERM-002), the
    /// read-only destination second (OW-UNITY-010). A <i>read</i> gets its
    /// allowed-paths half from <see cref="ReadRoots"/>, which reshapes the
    /// snapshot it is handed and never reads a file either.
    /// </para>
    /// <para>
    /// <b>Normalization.</b> The collapsing rules mirror the ones the two
    /// already-merged readers of the same SPEC clause use
    /// (<c>EditorRequirementEvaluator</c> and Core's D6 phase): empty and
    /// <c>.</c> segments collapse, <c>\</c> becomes <c>/</c>, and a path that
    /// cannot stay inside the project has no normalized form at all. The
    /// rooted/absolute/<c>~</c>/drive spellings reject before collapsing —
    /// otherwise their first segment would simply disappear and the rest
    /// would pass for a project-relative path. The bounded normalization
    /// helper is repeated here rather than widening another assembly's
    /// surface; the one containment reading the SPEC gives §G7 item 1 is
    /// shared instead — <see cref="IsInsideAllowedPaths(string, IReadOnlyList{string})"/>
    /// judges a resolved write path through the evaluator's own internal
    /// helper, so the frozen-boundary check and the declared
    /// <c>path_writable</c> check can never drift apart. The §H4 item 5 settings-file
    /// test is the deliberate exception: the D6 phase, the §G1 evaluator and
    /// this policy all ask Core's internal <c>SettingsPathGuard</c>, so a
    /// Windows spelling of the settings file that one layer recognizes cannot
    /// be missed by another.
    /// </para>
    /// <para>
    /// <b>Read paths.</b> A read path is judged here for its shape alone,
    /// because §G7 item 1 makes <c>Packages/</c> "readable only" — a search
    /// may look inside a package. The other half of the read contract is the
    /// <i>set of roots</i> a search may cover, which <c>unity.asset.find</c>
    /// asks this policy to compute (<see cref="ReadRoots"/>): the active
    /// Run's frozen <c>allowedPaths</c> plus the <c>Packages/</c> read-only
    /// root. Only the caller that actually performs the read knows which
    /// paths it is about to search, so the bound is enforced there and this
    /// policy answers containment only.
    /// </para>
    /// </remarks>
    internal static class AssetPathPolicy
    {
        /// <summary>The project prefix whose contents are readable only (SPEC §G7 item 1).</summary>
        internal const string PackagesPrefix = "Packages/";

        private const string ShapeMessageTemplate =
            "The asset path of port '{0}' is not a project-relative asset path.";

        private const string ShapeHint =
            "Use a '/'-separated path inside the project; a path containing '..' " +
            "and a rooted, absolute, drive-letter or '~' path are rejected.";

        private const string ReadOnlyMessageTemplate =
            "The asset path of port '{0}' lies in a read-only package.";

        private const string ReadOnlyHint =
            "Only embedded and local packages are writable; write under a path " +
            "inside the allowed paths.";

        private const string OutsideMessageTemplate =
            "The path of port '{0}' is outside the allowed paths.";

        private const string OutsideHint =
            "Search only inside the allowed paths of the Run or under the " +
            "Packages folder.";

        /// <summary>
        /// The normalized <c>/</c>-separated form of <paramref name="path"/>,
        /// or <see langword="null"/> when the text names no path inside the
        /// project: empty or whitespace-free input, a <c>..</c> segment, a
        /// rooted/absolute/<c>~</c>/drive-letter spelling and a path that
        /// collapses to nothing all have no normalized form.
        /// </summary>
        internal static string? Normalize(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            if (path![0] == '/' || path[0] == '\\' || path[0] == '~' ||
                (path.Length > 1 && char.IsLetter(path[0]) && path[1] == ':'))
            {
                return null;
            }
            var segments = path.Replace('\\', '/').Split('/');
            var kept = new List<string>(segments.Length);
            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                if (segment == "..")
                {
                    return null;
                }
                if (segment.Length == 0 || segment == ".")
                {
                    continue;
                }
                kept.Add(segment);
            }
            return kept.Count == 0 ? string.Empty : string.Join("/", kept);
        }

        /// <summary>
        /// Checks one read path (SPEC §G7 item 1): only its shape is judged,
        /// because a read may target anything under <c>Assets/</c> or
        /// <c>Packages/</c>. Returns the normalized path, or a failure whose
        /// single diagnostic is OW-PERM-002.
        /// </summary>
        internal static Result<string> CheckReadPath(string? path, string port)
        {
            var normalized = Normalize(path);
            return normalized is null || normalized.Length == 0
                ? Result<string>.Failure(InvalidPath(port))
                : Result<string>.Success(normalized);
        }

        /// <summary>
        /// The roots a read may cover (SPEC §G7 item 1): the normalized
        /// <c>allowedPaths</c> of the active Run's settings snapshot, always
        /// extended with the read-only <c>Packages/</c> root. A null or empty
        /// snapshot contributes nothing — with no snapshot the search may read
        /// <c>Packages/</c> only, the same fail-closed reading the
        /// <c>path_writable</c> evaluator applies to an empty allowed set.
        /// An entry that has no normalized form is skipped rather than
        /// degrading to a match-all root.
        /// </summary>
        /// <param name="allowedPaths">
        /// The frozen snapshot, or <see langword="null"/> when the active Run
        /// recorded none. Callers read it once per operation, never from the
        /// settings file again.
        /// </param>
        internal static IReadOnlyList<string> ReadRoots(IReadOnlyList<string>? allowedPaths)
        {
            var roots = new List<string>();
            if (allowedPaths is not null)
            {
                foreach (var prefix in RootPrefixes(allowedPaths))
                {
                    roots.Add(prefix);
                }
            }
            if (!roots.Contains(PackagesPrefix))
            {
                roots.Add(PackagesPrefix);
            }
            return roots;
        }

        /// <summary>
        /// Each readable path of <paramref name="paths"/> as a closed root
        /// prefix — normalized and ended with <c>/</c> — duplicates and paths
        /// with no normalized form dropped. Searching by prefix is what keeps
        /// <c>Assets/AllowedA</c> from opening <c>Assets/AllowedABroad/</c>.
        /// </summary>
        internal static IReadOnlyList<string> RootPrefixes(IEnumerable<string> paths)
        {
            var prefixes = new List<string>();
            foreach (var path in paths)
            {
                var normalized = Normalize(path);
                if (string.IsNullOrEmpty(normalized))
                {
                    continue;
                }
                var prefix = normalized.EndsWith("/", StringComparison.Ordinal)
                    ? normalized
                    : normalized + "/";
                if (!prefixes.Contains(prefix))
                {
                    prefixes.Add(prefix);
                }
            }
            return prefixes;
        }

        /// <summary>
        /// Whether the normalized <paramref name="path"/> is one of the
        /// <paramref name="prefixes"/> or lies inside one. The prefix itself
        /// counts as inside — a folder is an asset a search reports too. The
        /// comparison is case-insensitive, the containment the §G1 evaluator
        /// applies to the same clause.
        /// </summary>
        internal static bool IsInsideAnyRoot(
            string path, IReadOnlyList<string> prefixes)
        {
            foreach (var prefix in prefixes)
            {
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                var folder = SearchFolder(prefix);
                if (string.Equals(path, folder, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The folder spelling of a root prefix for <c>AssetDatabase.FindAssets</c>:
        /// the prefix with its closing <c>/</c> removed.
        /// </summary>
        internal static string SearchFolder(string prefix) =>
            prefix.EndsWith("/", StringComparison.Ordinal)
                ? prefix.Substring(0, prefix.Length - 1)
                : prefix;

        /// <summary>
        /// Checks one write path (SPEC §G7 item 1): the shape first
        /// (OW-PERM-002), then the read-only package half (OW-UNITY-010).
        /// Returns the normalized path, or a failure carrying the one
        /// diagnostic that refuses it.
        /// </summary>
        internal static Result<string> CheckWritePath(string? path, string port)
        {
            var shape = CheckReadPath(path, port);
            if (!shape.Ok)
            {
                return shape;
            }
            var normalized = shape.Value!;
            // SPEC §H4 item 5: the OpWeave settings file is never a writable
            // destination for a standard Operation — refused like a path
            // outside the allowed set, before the read-only half is judged.
            // This check is defence in depth: a standard Operation binds its
            // path port to the unity.AssetPath type, whose codec admits
            // Assets/ and Packages/ only, so a ProjectSettings/ path is
            // already refused before it gets here — it stays because a custom
            // Operation may declare its path_writable requirement on a plain
            // core.String port no codec narrows. The shared Core guard answers
            // it, covering the Windows spellings of the same file: a trailing
            // '.' or ' ', an NTFS stream suffix, an 8.3 short name.
            if (SettingsPathGuard.IsProtectedSettingsPath(normalized))
            {
                return Result<string>.Failure(OutsideAllowedPaths(port));
            }
            return IsInImmutablePackage(normalized)
                ? Result<string>.Failure(ReadOnlyPath(port))
                : Result<string>.Success(normalized);
        }

        /// <summary>
        /// Whether a path the asset database resolved — the move source or
        /// delete target a Ref-bound write actually mutates, which no
        /// declared path port covers — lies inside the Run's frozen
        /// <paramref name="allowedPaths"/> (SPEC §G7 item 1). The
        /// containment is the declared write-port rule itself —
        /// <c>EditorRequirementEvaluator.IsInsideAllowedPaths</c>, a path
        /// counts only when it begins with a normalized allowed root plus
        /// <c>/</c>, so the root folder itself is outside — shared through
        /// that one helper so the two readings of the clause cannot drift.
        /// An empty set allows nothing, and a <see langword="null"/>
        /// snapshot is no better: the frozen boundary a Run's acceptance
        /// validated lives in memory only — nothing durable carries it —
        /// so a re-attached Run whose snapshot is gone must fail closed
        /// (OW-PERM-002 at the port) rather than read the absence as
        /// "everything is allowed" (§H4.1, decision D-R6).
        /// </summary>
        internal static bool IsInsideAllowedPaths(
            string assetPath, IReadOnlyList<string>? allowedPaths)
        {
            var normalized = Normalize(assetPath);
            return normalized is not null && allowedPaths is not null &&
                EditorRequirementEvaluator.IsInsideAllowedPaths(
                    normalized, RootPrefixes(allowedPaths));
        }

        /// <summary>
        /// Whether <paramref name="normalizedPath"/> lies in a package v1
        /// treats as immutable (SPEC §G1 item 4, §G7 item 1): only embedded
        /// and local (<c>file:</c>) packages are writable project sources, so
        /// a registry, Git, built-in or tarball package — and a
        /// <c>Packages/</c> path no package owns, which the package manager
        /// cannot classify at all — is read-only. A path outside
        /// <c>Packages/</c> belongs to the project and is never a package.
        /// </summary>
        internal static bool IsInImmutablePackage(string normalizedPath)
        {
            if (!normalizedPath.StartsWith(PackagesPrefix, StringComparison.Ordinal))
            {
                return false;
            }
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(normalizedPath);
            return info is null ||
                (info.source != PackageSource.Embedded &&
                 info.source != PackageSource.Local);
        }

        /// <summary>
        /// The OW-PERM-002 refusal of a path that names no project asset path.
        /// The rejected text is never echoed — the port name identifies it.
        /// </summary>
        internal static Diagnostic InvalidPath(string port) =>
            ErrorCodes.Create(
                ErrorCodes.OW_PERM_002,
                string.Format(ShapeMessageTemplate, port),
                port: port,
                hint: ShapeHint);

        /// <summary>The OW-UNITY-010 refusal of a destination in a read-only package.</summary>
        internal static Diagnostic ReadOnlyPath(string port) =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_010,
                string.Format(ReadOnlyMessageTemplate, port),
                port: port,
                hint: ReadOnlyHint);

        /// <summary>
        /// The OW-PERM-002 refusal of a path outside what the Run may read or
        /// write. The wording is the §G1 evaluator's own for the same clause;
        /// like its sibling refusals it names the port, never the rejected
        /// path.
        /// </summary>
        internal static Diagnostic OutsideAllowedPaths(string port) =>
            ErrorCodes.Create(
                ErrorCodes.OW_PERM_002,
                string.Format(OutsideMessageTemplate, port),
                port: port,
                hint: OutsideHint);
    }
}
