#nullable enable

using System;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;

namespace OpWeave.Operations
{
    /// <summary>
    /// <c>unity.asset.create_folder</c> (SPEC §I.3, §G7 item 1): creates the
    /// folder at <c>path</c> together with every parent folder that does not
    /// exist yet. An already existing folder is a success that changes
    /// nothing, which is what makes the Operation
    /// <see cref="Idempotency.Idempotent"/> (§I.3 note "既存なら成功").
    /// </summary>
    /// <remarks>
    /// <para>
    /// The path is normalized to <c>/</c> separators and judged by
    /// <see cref="AssetPathPolicy"/> before anything touches the asset
    /// database. The write port also declares the §G1
    /// <c>path_writable</c> requirement, so a constant path outside the
    /// settings <c>allowedPaths</c> is already OW-PERM-002 at validation time
    /// and a value that arrives through a data edge is judged when the Run
    /// reaches the step. A parent the Editor cannot create — a name that
    /// collides with an existing asset, or a <c>Packages/</c> folder, which
    /// <c>AssetDatabase.CreateFolder</c> only offers under <c>Assets/</c> —
    /// fails OW-UNITY-005, the §J code for an invalid path or a collision
    /// with an existing asset (§G7's error row).
    /// </para>
    /// <para>
    /// <see cref="Preflight"/> predicts no <see cref="PlannedChange"/>: the
    /// SG-06 <see cref="StepContext"/> carries no node identity, which
    /// <see cref="PlannedChange"/> requires, so the node-bound prediction of a
    /// Dry Run (SPEC §D8 item 2) belongs to the side that knows the node, not
    /// to the Operation.
    /// </para>
    /// <para>
    /// Descriptor fields outside the §0.6 SG-05 fluent set are set on the
    /// record itself, as <c>core.start</c> does.
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class CreateFolderOperation : OperationBase
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.asset.create_folder";

        /// <summary>The name of the required asset-path input and of the output echoing it.</summary>
        internal const string PathPort = "path";

        private static readonly JsonValue s_emptySnapshot =
            JsonValue.From(new JObject());

        /// <inheritdoc/>
        public override OperationDescriptor Descriptor { get; } = BuildDescriptor();

        /// <inheritdoc/>
        public override PreflightResult Preflight(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            // SPEC §D8 item 1: an edge-fed port has no value to inspect in a Dry Run.
            if (ctx.IsPending(PathPort))
            {
                return base.Preflight(ctx);
            }
            var check = AssetPathPolicy.CheckWritePath(ctx.Get<string>(PathPort), PathPort);
            return check.Ok
                ? new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    JsonValue.From(new JObject { ["path"] = check.Value }))
                : new PreflightResult(check.Diagnostics, Array.Empty<PlannedChange>(), s_emptySnapshot);
        }

        /// <inheritdoc/>
        public override StepResult Execute(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            var check = AssetPathPolicy.CheckWritePath(ctx.Get<string>(PathPort), PathPort);
            if (!check.Ok)
            {
                return StepResult.Failed(check.Diagnostics[0]);
            }
            var path = check.Value!;

            // The folder already being there is the idempotent outcome: nothing
            // is created, so nothing is recorded as changed either.
            if (AssetDatabase.IsValidFolder(path))
            {
                ctx.SetOutput(PathPort, path);
                return StepResult.Succeeded();
            }

            var failure = CreateFolders(path);
            if (failure is not null)
            {
                return StepResult.Failed(failure);
            }

            // §I.3 declares Undo None, so the recorded change is not undoable.
            ctx.Changes.Created(new ChangeTarget("asset", path), undoable: false);
            ctx.SetOutput(PathPort, path);
            return StepResult.Succeeded();
        }

        // Creates every missing segment of the normalized path, outermost
        // first: AssetDatabase.CreateFolder takes the parent folder's path and
        // requires all of its own ancestors to exist already, and answers an
        // empty string when it cannot create the folder.
        //
        // A name an asset already holds is refused here rather than left to
        // CreateFolder's answer: the §J contract is OW-UNITY-005 whenever the
        // path is not free, and an Operation that reports success for a folder
        // it did not create would leave the Run's change record claiming a
        // creation the project never got. The Editor's other two answers are
        // checked too — an empty one, and a GUID naming a folder other than the
        // requested one: when the name is taken the Editor silently creates
        // "taken 1" instead, so the returned GUID is resolved back to its path
        // and must match exactly; that renamed folder is not what was asked for,
        // so it is deleted again instead of being left behind by a failure.
        private static Diagnostic? CreateFolders(string path)
        {
            var segments = path.Split('/');
            var parent = segments[0];
            for (var i = 1; i < segments.Length; i++)
            {
                var child = parent + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(child))
                {
                    if (AssetDatabase.AssetPathToGUID(child).Length > 0)
                    {
                        return NotCreated();
                    }
                    var guid = AssetDatabase.CreateFolder(parent, segments[i]);
                    var created = string.IsNullOrEmpty(guid)
                        ? string.Empty
                        : AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.Equals(created, child, StringComparison.Ordinal))
                    {
                        if (created.Length > 0)
                        {
                            AssetDatabase.DeleteAsset(created);
                        }
                        return NotCreated();
                    }
                    if (!AssetDatabase.IsValidFolder(child))
                    {
                        return NotCreated();
                    }
                }
                parent = child;
            }
            return null;
        }

        // The §J code for an invalid path or a collision with an existing
        // asset (§G7's error row).
        private static Diagnostic NotCreated() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_005,
                "The asset folder could not be created.",
                port: PathPort,
                hint: "A folder cannot share its name with an existing asset, and " +
                    "folders can only be created under the Assets folder.");

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Creates an asset folder and any missing parent folders; an existing folder is a success.")
            .Ja("フォルダ作成",
                "アセットフォルダを、無い親フォルダと一緒に作成します。既存のフォルダは成功になります。",
                "フォルダ", "作成", "アセット")
            .Input(PathPort, "unity.AssetPath", required: true)
            .Output(PathPort, "unity.AssetPath")
            .Effects(EffectNames.AssetCreate)
            .Undo(UndoSupport.None)
            .Idempotency(Idempotency.Idempotent)
            // SPEC §G1 item 4 / §G7 item 1: the allowed-paths and writability
            // half of the asset path policy, judged with the settings snapshot
            // this Run's acceptance validated (§H4.1, decision D-R6).
            .Requires("path_writable", PathPort)
            .Build() with
            {
                DisplayName = "Create Folder",
                Description =
                    "Creates the folder at the given project asset path together with every " +
                    "parent folder that does not exist yet. Running it again on a folder that " +
                    "already exists succeeds and changes nothing. The path is normalized to '/' " +
                    "separators; a path containing '..' or spelled as a rooted or absolute path " +
                    "fails with OW-PERM-002, a path outside the allowed paths fails with " +
                    "OW-PERM-002 as well, and a destination inside a read-only package fails " +
                    "with OW-UNITY-010.",
                Category = "Asset",
                Tags = new[] { "asset", "folder" },
                Keywords = new[] { "folder", "create", "directory" },
                Examples = new[]
                {
                    new OperationExample(
                        "Create the folder a later step writes reports into",
                        AssetOperationExamples.CreateFolder),
                },
            };
    }
}
