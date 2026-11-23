#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;

namespace OpWeave.Operations
{
    /// <summary>
    /// <c>unity.asset.find</c> (SPEC §I.3, §G7 item 6): runs
    /// <c>AssetDatabase.FindAssets</c> with the given search filter, restricted
    /// to the folders it is given and always to what the Run may read
    /// (§G7 item 1), and returns at most
    /// <see cref="MaxResults"/> <c>unity.AssetRef&lt;unity.Object&gt;</c>
    /// values — §G7 item 6 caps the result at 500 and requires a
    /// <c>truncated</c> marker when more matched. Read-only,
    /// <see cref="ExecutionMode.Immediate"/> and
    /// <see cref="Idempotency.Idempotent"/>; it records nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The <c>truncated</c> output.</b> §G7 item 6 states the marker but
    /// the §I.3 output column lists only <c>assets</c>. An Operation can only
    /// report through a declared output port (any other value the step writes
    /// is dropped before it reaches the journal — SPEC §E5), so the marker is
    /// a <c>core.Bool</c> output port beside <c>assets</c>; the ticket's
    /// acceptance criterion for this operation names the flag too. The choice
    /// is recorded in the implementation PR for review.
    /// </para>
    /// <para>
    /// <b>Ref shape.</b> Each hit becomes <c>{kind:"asset", guid, path}</c> —
    /// exactly the §B5 <c>unity.AssetRef</c> key set a main asset carries, and
    /// exactly what <c>FindAssets</c> reports. The assets are deliberately not
    /// loaded: §G7 item 6 asks for the search result, and materializing up to
    /// 500 assets per call would import and hold every one of them.
    /// <c>localId</c> belongs to sub-assets, which a search never returns.
    /// </para>
    /// <para>
    /// <b>Path policy.</b> A folder is a read path, so its shape is judged by
    /// <see cref="AssetPathPolicy"/> (<c>..</c> or a rooted spelling is
    /// OW-PERM-002), and §G7 item 1 then bounds <i>what the search may
    /// cover</i>: the active Run's frozen <c>allowedPaths</c> plus the
    /// read-only <c>Packages/</c> root. An explicit folder outside those roots
    /// is OW-PERM-002 like any other path outside the allowed paths, and an
    /// omitted or empty <c>folders</c> searches those roots themselves — never
    /// the whole project, which would read whatever the Plan was never
    /// authorized to see. With no settings snapshot the roots are
    /// <c>Packages/</c> alone (fail closed, as the <c>path_writable</c>
    /// evaluator does with an empty allowed set). A result that still lies
    /// outside the searched folders is dropped, so the bound holds on the
    /// search's output as well as on its input.
    /// </para>
    /// <para>
    /// <b>The folder spelling.</b> The live binding is the
    /// <c>params string[]</c> overload, whose documented case for a folder
    /// list is an array of project-relative folder paths, never a
    /// <see langword="null"/> one.
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class FindAssetsOperation : OperationBase
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.asset.find";

        /// <summary>The required search-filter input port (§I.3 "filter*: String").</summary>
        internal const string FilterPort = "filter";

        /// <summary>The optional folders input port (§I.3 "folders: List&lt;AssetPath&gt;").</summary>
        internal const string FoldersPort = "folders";

        /// <summary>The asset-reference list output port (§I.3 "assets").</summary>
        internal const string AssetsPort = "assets";

        /// <summary>The output marker of §G7 item 6: more than <see cref="MaxResults"/> matched.</summary>
        internal const string TruncatedPort = "truncated";

        /// <summary>The §G7 item 6 result cap.</summary>
        internal const int MaxResults = 500;

        // The §B5 kind discriminator of unity.AssetRef<T> (§G2 "asset").
        private const string AssetRefKind = "asset";

        private static readonly JsonValue s_emptySnapshot =
            JsonValue.From(new JObject());

        private readonly Func<string, string[], string[]> _findAssets;
        private readonly Func<string, string> _guidToAssetPath;

        // The allowedPaths of the settings snapshot the active Run's acceptance
        // validated (§H4.1, decision D-R6) — the same authority the
        // path_writable evaluator judges by, and null when no Run recorded one.
        private readonly Func<IReadOnlyList<string>?> _allowedPaths;

        /// <summary>Creates the Operation over the live asset database.</summary>
        public FindAssetsOperation()
            : this(AssetDatabase.FindAssets, AssetDatabase.GUIDToAssetPath, CurrentRunAllowedPaths)
        {
        }

        /// <summary>
        /// The test seam over the two asset-database reads the step performs —
        /// the same <see cref="EditorStateProbe"/> pattern the UnityAdapter uses,
        /// so the 500-result cap and the folder restriction are provable without
        /// creating 501 assets in the project — and over the Run's read roots,
        /// which no test can accept through a Run either.
        /// <paramref name="allowedPaths"/> answers <see langword="null"/> for
        /// "no active Run recorded a snapshot".
        /// </summary>
        internal FindAssetsOperation(
            Func<string, string[], string[]> findAssets,
            Func<string, string> guidToAssetPath,
            Func<IReadOnlyList<string>?> allowedPaths)
        {
            _findAssets = findAssets ?? throw new ArgumentNullException(nameof(findAssets));
            _guidToAssetPath =
                guidToAssetPath ?? throw new ArgumentNullException(nameof(guidToAssetPath));
            _allowedPaths = allowedPaths ?? throw new ArgumentNullException(nameof(allowedPaths));
        }

        // The frozen snapshot of the Run currently executing, read through the
        // host accessor the requirement evaluator uses — never the settings
        // file, which a mid-Run edit could not be allowed to loosen.
        private static IReadOnlyList<string>? CurrentRunAllowedPaths() =>
            OpWeaveRunnerHost.Current?.ActiveRunAllowedPaths;

        /// <inheritdoc/>
        public override OperationDescriptor Descriptor { get; } = BuildDescriptor();

        /// <inheritdoc/>
        public override PreflightResult Preflight(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            if (!ctx.IsPresent(FoldersPort) || ctx.IsPending(FoldersPort))
            {
                return base.Preflight(ctx);
            }
            var folders = CheckFolders(ctx, AssetPathPolicy.ReadRoots(_allowedPaths()));
            return folders.Ok
                ? base.Preflight(ctx)
                : new PreflightResult(
                    folders.Diagnostics,
                    Array.Empty<PlannedChange>(),
                    s_emptySnapshot);
        }

        /// <inheritdoc/>
        public override StepResult Execute(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            var roots = AssetPathPolicy.ReadRoots(_allowedPaths());
            var folders = ctx.IsPresent(FoldersPort)
                ? CheckFolders(ctx, roots)
                : Result<string[]>.Success(SearchFolders(roots));
            if (!folders.Ok)
            {
                return StepResult.Failed(folders.Diagnostics[0]);
            }

            // The bound re-applied to the search's own output: what FindAssets
            // reports must lie inside the folders this step was allowed to
            // search, whatever the search did with them.
            var scope = AssetPathPolicy.RootPrefixes(folders.Value!);

            var guids = _findAssets(ctx.Get<string>(FilterPort), folders.Value!);

            var assets = new JArray();
            var taken = Math.Min(guids.Length, MaxResults);
            for (var i = 0; i < taken; i++)
            {
                var guid = guids[i];
                var path = _guidToAssetPath(guid);
                // A guid the database no longer maps to a path is a stale hit
                // of a search that ran while the asset was being removed; it
                // carries no Ref and never displaces a live result.
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }
                var normalized = AssetPathPolicy.Normalize(path);
                if (normalized is null || !AssetPathPolicy.IsInsideAnyRoot(normalized, scope))
                {
                    continue;
                }
                assets.Add(new JObject
                {
                    ["kind"] = AssetRefKind,
                    ["guid"] = guid,
                    ["path"] = path,
                });
            }

            ctx.SetOutput(AssetsPort, JsonValue.From(assets));
            ctx.SetOutput(TruncatedPort, guids.Length > MaxResults);
            return StepResult.Succeeded();
        }

        // The normalized folder list, each inside a readable root, or an
        // OW-PERM-002 refusal of the first one that is not (§G7 item 1).
        private static Result<string[]> CheckFolders(
            StepContext ctx, IReadOnlyList<string> roots)
        {
            var token = ctx.Get<JsonValue>(FoldersPort).ToToken();
            if (token is not JArray array || array.Count == 0)
            {
                return Result<string[]>.Success(SearchFolders(roots));
            }
            var folders = new string[array.Count];
            for (var i = 0; i < array.Count; i++)
            {
                var entry = array[i];
                var check = AssetPathPolicy.CheckReadPath(
                    entry.Type == JTokenType.String ? (string?)entry : null,
                    FoldersPort);
                if (!check.Ok)
                {
                    return Result<string[]>.Failure(check.Diagnostics);
                }
                var normalized = check.Value!;
                if (!AssetPathPolicy.IsInsideAnyRoot(normalized, roots))
                {
                    return Result<string[]>.Failure(
                        AssetPathPolicy.OutsideAllowedPaths(FoldersPort));
                }
                folders[i] = normalized;
            }
            return Result<string[]>.Success(folders);
        }

        // What an omitted or empty folders port searches: the readable roots
        // themselves in the folder spelling the search takes. The list is never
        // empty — Packages/ is always one of the roots — so the step never
        // hands the Editor the unbounded search of an empty folder array.
        private static string[] SearchFolders(IReadOnlyList<string> roots)
        {
            var folders = new string[roots.Count];
            for (var i = 0; i < roots.Count; i++)
            {
                folders[i] = AssetPathPolicy.SearchFolder(roots[i]);
            }
            return folders;
        }

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Finds assets with a search filter and returns at most 500 references plus a truncated flag.")
            .Ja("アセット検索",
                "検索フィルターでアセットを探し、最大500件の参照と切り詰めフラグを返します。",
                "検索", "アセット", "絞り込み")
            .Input(FilterPort, "core.String", required: true)
            .Input(FoldersPort, "core.List<unity.AssetPath>", required: false)
            .Output(AssetsPort, "core.List<unity.AssetRef<unity.Object>>")
            .Output(TruncatedPort, "core.Bool")
            .Effects(EffectNames.Read)
            .Undo(UndoSupport.None)
            .Idempotency(Idempotency.Idempotent)
            .Build() with
            {
                DisplayName = "Find Assets",
                Description =
                    "Searches the project with an AssetDatabase search filter — inside the " +
                    "given folders, or inside the allowed paths of the Run and the Packages " +
                    "folder when none are given — and returns the matches as asset " +
                    "references. At most 500 references are returned; when more assets " +
                    "matched, the truncated output is true. Searching is read-only, so a " +
                    "folder inside a read-only package may be searched; a folder path " +
                    "containing '..' or spelled as a rooted or absolute path, and a folder " +
                    "outside the allowed paths and the Packages folder, fail with " +
                    "OW-PERM-002.",
                Category = "Asset",
                Tags = new[] { "asset", "search" },
                Keywords = new[] { "search", "find", "query" },
                Examples = new[]
                {
                    new OperationExample(
                        "List the materials under Assets/Materials",
                        AssetOperationExamples.Find),
                },
            };
    }
}
