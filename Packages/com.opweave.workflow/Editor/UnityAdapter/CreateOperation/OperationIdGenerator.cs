#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using OpWeave.Core;
using OpWeave.Core.Registry;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The generated Operation ID of SPEC §A4 / §M.4 item 2: a UUID v4
    /// drawn from a cryptographically secure source, rendered as
    /// <c>proj.op_</c> + 32 lowercase hex digits (40 characters), minted
    /// exactly once when the Create dialog commits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Resolve"/> is the commit-time check of SPEC §M.4 item 2:
    /// before any file is written the drawn ID is validated against the A4
    /// grammar (<see cref="IdGrammar.IsValidOperationId"/>, which carries
    /// the 128-character bound), the reserved namespaces (OW-OP-007), the
    /// published Registry, and the Operation IDs declared in existing
    /// files under the target folder and <c>customOperationFolders</c>.
    /// A collision redraws up to <see cref="MaxGenerationAttempts"/> times
    /// and then fails with an explicit message that names the collision.
    /// A grammar or reserved-namespace draw cannot be produced by the
    /// default source, so it fails on the spot instead of being retried.
    /// </para>
    /// <para>
    /// The ID source is injectable at
    /// <see cref="OperationCreateWriter.Write"/> so tests can force a
    /// collision, count the draws, and drive the OW-OP-007 path that the
    /// dialog can no longer reach through user input. Nothing outside the
    /// commit path calls the source: the live-validation planner, redraws
    /// and previews never mint an ID.
    /// </para>
    /// </remarks>
    internal static class OperationIdGenerator
    {
        /// <summary>The reserved-prefix-free namespace of generated IDs.</summary>
        internal const string GeneratedIdPrefix = "proj.op_";

        /// <summary>The total length of a generated ID (prefix + 32 hex).</summary>
        internal const int GeneratedIdLength = 40;

        /// <summary>The bounded number of draws before the commit fails.</summary>
        internal const int MaxGenerationAttempts = 8;

        // [OpWeaveOperation("<id>" ... — generated files always use this
        // form; the optional qualifier covers hand-written files that name
        // the attribute with its full namespace, and (?:Attribute)? the
        // equally legal C# suffixed spelling [OpWeaveOperationAttribute(].
        private static readonly Regex s_declaredId =
            new Regex(
                "\\[\\s*(?:OpWeave\\.Core\\.Operations\\.)?OpWeaveOperation(?:Attribute)?\\s*\\(\\s*\"([^\"]+)\"",
                RegexOptions.CultureInvariant);

        /// <summary>
        /// The default ID source: one RFC 9562 UUID v4 from
        /// <see cref="Guid.NewGuid"/> (the OS cryptographically secure
        /// random source, with the version-4 and variant bits set),
        /// rendered as <c>proj.op_</c> + 32 lowercase hex digits.
        /// </summary>
        internal static string NewUuidV4Id() =>
            GeneratedIdPrefix + Guid.NewGuid().ToString("N");

        /// <summary>The outcome of the commit-time ID resolution.</summary>
        internal sealed class Resolution
        {
            /// <summary>The accepted ID; null until one passes every check.</summary>
            internal string? OperationId;

            /// <summary>Fixed English messages shown in the dialog.</summary>
            internal readonly List<string> Errors = new List<string>();

            internal bool Ok => Errors.Count == 0 && OperationId is not null;
        }

        /// <summary>
        /// Draws from <paramref name="source"/> and accepts the first ID
        /// that survives every commit-time check. Retries only on
        /// collisions; grammar and reserved-namespace draws fail
        /// immediately because the default source can never produce them.
        /// </summary>
        internal static Resolution Resolve(
            OperationCreatePlan plan,
            Func<string> source)
        {
            var resolution = new Resolution();
            var snapshot = RegistryPublication.Shared.Snapshot;
            var fileIds = CollectDeclaredIds(plan.TargetFolder);
            string? lastCollision = null;
            for (var attempt = 0; attempt < MaxGenerationAttempts; attempt++)
            {
                string? candidate;
                try
                {
                    candidate = source()?.Trim();
                }
                catch (Exception e)
                {
                    resolution.Errors.Add(
                        "The Operation ID source failed (" +
                        e.GetType().Name + "). No file was written or changed.");
                    return resolution;
                }
                if (!IdGrammar.IsValidOperationId(candidate))
                {
                    resolution.Errors.Add(
                        "The generated Operation ID '" + (candidate ?? "(empty)") +
                        "' does not satisfy the SPEC A4 Operation ID grammar.");
                    return resolution;
                }
                if (IdGrammar.HasReservedOperationPrefix(candidate))
                {
                    resolution.Errors.Add(
                        ErrorCodes.OW_OP_007 +
                        ": The generated Operation ID '" + candidate +
                        "' uses a reserved namespace (core., unity., opweave.).");
                    return resolution;
                }
                var collision = FindCollision(candidate!, snapshot, fileIds);
                if (collision is null)
                {
                    resolution.OperationId = candidate;
                    return resolution;
                }
                lastCollision = collision;
            }
            resolution.Errors.Add(
                "The generated Operation ID collided " +
                MaxGenerationAttempts +
                " times in a row; the last draw conflicts with " +
                lastCollision +
                ". No file was written or changed.");
            return resolution;
        }

        // A registered ID (any version) or an ID already declared in an
        // existing file under the watched folders; returns a short English
        // description that names the colliding ID for the error message.
        private static string? FindCollision(
            string candidate,
            RegistrySnapshot? snapshot,
            IReadOnlyDictionary<string, string> fileIds)
        {
            var registered = snapshot?.FindLatest(candidate);
            if (registered is not null)
            {
                return "the registered Operation '" + candidate +
                    "' (version " + registered.Descriptor.Version + ")";
            }
            if (fileIds.TryGetValue(candidate, out var path))
            {
                return "'" + candidate + "' declared in the existing file '" +
                    path + "'";
            }
            return null;
        }

        // Every Operation ID declared in an [OpWeaveOperation("...")]
        // attribute inside the .cs files under the target folder and each
        // customOperationFolders entry. Generated files use the same
        // attribute form, so the scan covers the generated files of SPEC
        // §M.4 item 2; scanning all files also protects hand-written IDs
        // a snapshot cannot know about yet (not compiled or not built).
        private static Dictionary<string, string> CollectDeclaredIds(
            string targetFolder)
        {
            var folders = new List<string> { targetFolder };
            var loaded = OpWeaveSettingsLoader.Load();
            var settings =
                loaded.Ok ? loaded.Value! : OpWeaveSettings.Default;
            for (var i = 0; i < settings.CustomOperationFolders.Count; i++)
            {
                folders.Add(settings.CustomOperationFolders[i]);
            }
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            var seenFolders = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < folders.Count; i++)
            {
                var folder = OperationCreatePlanner.NormalizeFolder(folders[i]);
                if (folder.Length == 0 || !seenFolders.Add(folder))
                {
                    continue;
                }
                string[] files;
                try
                {
                    var absolute = OperationCreatePlanner.ProjectAbsolute(folder);
                    if (!Directory.Exists(absolute))
                    {
                        continue;
                    }
                    files = Directory.GetFiles(
                        absolute, "*.cs", SearchOption.AllDirectories);
                }
                catch (Exception)
                {
                    continue;
                }
                for (var j = 0; j < files.Length; j++)
                {
                    try
                    {
                        foreach (Match match in s_declaredId.Matches(
                            File.ReadAllText(files[j])))
                        {
                            var id = match.Groups[1].Value;
                            if (!ids.ContainsKey(id))
                            {
                                ids.Add(id, files[j]);
                            }
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            return ids;
        }
    }
}
