#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;

namespace OpWeave.Operations
{
    /// <summary>
    /// <c>unity.asset.import</c> (SPEC §I.3, §G7 item 2): imports a file that
    /// is already on disk through
    /// <c>AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate)</c>
    /// and waits on the SPEC §G4 <see cref="AssetImportedCondition"/> until
    /// the main asset at the path loads —
    /// <see cref="ExecutionMode.Waitable"/>, Undo
    /// <see cref="UndoSupport.None"/> and
    /// <see cref="Idempotency.Idempotent"/>: importing an already imported
    /// file succeeds and changes nothing the Plan can observe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The §I.3 precondition.</b> "ファイルがディスクにあること" — the
    /// file must exist on disk. It is judged at step time (and reported in a
    /// Dry Run's Preflight), because a file can appear or vanish between
    /// validation and execution; a missing file is refused with
    /// OW-UNITY-008, the §J import-failure code. The path itself is a write
    /// path — the import creates the asset at it — so it carries the §G1
    /// <c>path_writable</c> requirement like the other Asset Operations:
    /// a <c>..</c> or rooted spelling is OW-PERM-002 and a read-only package
    /// destination is OW-UNITY-010.
    /// </para>
    /// <para>
    /// <b>The wait.</b> <c>ImportAsset</c> with <c>ForceUpdate</c> only queues
    /// the import, so Execute returns
    /// <see cref="StepResult.Waiting"/> with the AssetImported token; each
    /// <see cref="Poll"/> asks the condition — Pending hands back the updated
    /// resume token (the consecutive-null streak and the captured import
    /// errors, §G4 "条件は resumeToken だけから復元できる"), Done sets the
    /// <c>asset</c> output to the loaded main asset's fully-keyed Ref, and
    /// Failed fails the step with the OW-UNITY-008 the condition attached
    /// the step's import errors to. Nothing else is kept in the Operation:
    /// SPEC §C2 item 4 forbids state outside the resume token.
    /// </para>
    /// <para>
    /// <b>The change record.</b> The step records nothing through
    /// <see cref="StepContext.Changes"/>: the import completes after
    /// Execute returned, so <c>ctx.Changes</c> cannot name it — the §F7
    /// second channel is exactly for this: the imported path the Editor
    /// reports through <c>OnPostprocessAllAssets</c> is attributed to this
    /// step's attempt while the Run is still executing
    /// (<see cref="ChangeSetTracker"/>).
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class ImportAssetOperation : OperationBase
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.asset.import";

        /// <summary>The name of the required asset-path input port.</summary>
        internal const string PathPort = "path";

        /// <summary>The name of the imported-asset reference output port.</summary>
        internal const string AssetPort = "asset";

        /// <summary>The fixed English label journaled as StepWaiting waitingFor (SPEC §F2).</summary>
        internal const string WaitingForLabel = "asset import";

        private static readonly JsonValue s_emptySnapshot =
            JsonValue.From(new JObject());

        private readonly Func<string, bool> _fileOnDisk;
        private readonly Action<string> _importAsset;
        private readonly Func<string, UnityEngine.Object?> _loadMainAsset;
        private readonly IWaitCondition _condition;

        /// <summary>Creates the Operation over the live asset database.</summary>
        public ImportAssetOperation()
            : this(
                static path => AssetDatabase.ImportAsset(
                    path, ImportAssetOptions.ForceUpdate),
                FileOnDisk,
                static path => AssetDatabase.LoadMainAssetAtPath(path),
                new AssetImportedCondition())
        {
        }

        /// <summary>
        /// The test seams over the disk read, the queued import, the main
        /// asset load and the wait condition — the
        /// <see cref="EditorStateProbe"/> pattern, so the Waitable lifecycle
        /// is provable without driving a real asynchronous import.
        /// </summary>
        /// <exception cref="ArgumentNullException">Any argument is null.</exception>
        internal ImportAssetOperation(
            Action<string> importAsset,
            Func<string, bool> fileOnDisk,
            Func<string, UnityEngine.Object?> loadMainAsset,
            IWaitCondition condition)
        {
            _importAsset = importAsset ?? throw new ArgumentNullException(nameof(importAsset));
            _fileOnDisk = fileOnDisk ?? throw new ArgumentNullException(nameof(fileOnDisk));
            _loadMainAsset =
                loadMainAsset ?? throw new ArgumentNullException(nameof(loadMainAsset));
            _condition = condition ?? throw new ArgumentNullException(nameof(condition));
        }

        private static bool FileOnDisk(string assetPath)
        {
            var physical = AssetArtifact.ToPhysicalPath(assetPath);
            return physical is not null && File.Exists(physical);
        }

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
            var check = AssetPathPolicy.CheckWritePath(
                ctx.Get<string>(PathPort), PathPort);
            if (!check.Ok)
            {
                return new PreflightResult(
                    check.Diagnostics, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }
            var path = check.Value!;
            if (!_fileOnDisk(path))
            {
                return new PreflightResult(
                    new[] { FileMissing() }, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }
            // SPEC §D0.3 item 3: importing the file joins it to the domain
            // governing the path — judged before any side effect.
            var guarded = RunDefinitionGuard.CheckPath(
                path, GuardedPathRole.Destination, contentPath: path);
            if (guarded is not null)
            {
                return new PreflightResult(
                    new[] { guarded }, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }
            return new PreflightResult(
                Array.Empty<Diagnostic>(),
                Array.Empty<PlannedChange>(),
                JsonValue.From(new JObject { [PathPort] = path }));
        }

        /// <inheritdoc/>
        public override StepResult Execute(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            var check = AssetPathPolicy.CheckWritePath(
                ctx.Get<string>(PathPort), PathPort);
            if (!check.Ok)
            {
                return StepResult.Failed(check.Diagnostics[0]);
            }
            var path = check.Value!;
            if (!_fileOnDisk(path))
            {
                return StepResult.Failed(FileMissing());
            }
            // SPEC §D0.3 item 4: re-checked here, on the main thread Run
            // acceptance serializes on, so a Run admitted since Preflight
            // still blocks the import — nothing is imported when it refuses.
            var guarded = RunDefinitionGuard.CheckPath(
                path, GuardedPathRole.Destination, contentPath: path);
            if (guarded is not null)
            {
                return StepResult.Failed(guarded);
            }
            // The step-start boundary of the §G4 error attribution: only
            // errors this step's own import logs may be attached to its
            // OW-UNITY-008, never an entry an earlier import left behind.
            ImportErrorCapture.BeginStep();
            _importAsset(path);
            // The queued import can already have failed — only the resume
            // token survives a reload, so its errors are folded in now.
            return StepResult.Waiting(
                AssetImportedCondition.InitialToken(
                    new[] { path }, ImportErrorCapture.RecentErrors),
                WaitingForLabel);
        }

        /// <inheritdoc/>
        public override StepResult Poll(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            var token = ctx.ResumeToken ?? throw new InvalidOperationException(
                "A Waitable Operation keeps its wait state only in the resume " +
                "token (SPEC §C2 item 4); none was handed back.");
            var status = _condition.Check(token);
            switch (status.Kind)
            {
                case WaitStatusKind.Done:
                    var path = AssetImportedCondition.PathsOf(token)[0];
                    var asset = _loadMainAsset(path);
                    if (asset is null)
                    {
                        // The condition just proved the load — a vanishing
                        // main asset is the import settling, so the wait
                        // simply continues rather than inventing a failure.
                        return StepResult.Waiting(token, WaitingForLabel);
                    }
                    // The step is finished: its §G4 capture slot is dropped
                    // so a later step never reads these entries.
                    ImportErrorCapture.EndStep();
                    ctx.SetOutput(AssetPort, asset);
                    return StepResult.Succeeded();
                case WaitStatusKind.Failed:
                    ImportErrorCapture.EndStep();
                    return StepResult.Failed(
                        status.Error is { } failure
                            ? failure with { Port = PathPort }
                            : throw new InvalidOperationException(
                                "A Failed WaitStatus carries its Diagnostic (SPEC §G4)."));
                default:
                    return StepResult.Waiting(status.ResumeToken!, WaitingForLabel);
            }
        }

        // The file-on-disk refusal of the §I.3 precondition: the import
        // cannot start, so OW-UNITY-008 is the §J code. The refused path is
        // never echoed — the port name identifies it.
        private static Diagnostic FileMissing() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_008,
                "The file to import does not exist on disk.",
                port: PathPort,
                hint: "The asset path of port 'path' must name a file that " +
                    "already exists inside the project.");

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Imports an existing on-disk file as an asset and waits for the import to finish.")
            .Ja("アセット取り込み",
                "ディスク上の既存ファイルをアセットとして取り込み、完了まで待ちます。",
                "インポート", "取り込み", "アセット")
            .Input(PathPort, "unity.AssetPath", required: true)
            .Output(AssetPort, "unity.AssetRef<unity.Object>")
            .Effects(EffectNames.AssetCreate)
            .Undo(UndoSupport.None)
            .Idempotency(Idempotency.Idempotent)
            // SPEC §G1 item 4 / §G7 item 1: the allowed-paths and writability
            // half of the asset path policy, judged with the settings snapshot
            // this Run's acceptance validated (§H4.1, decision D-R6).
            .Requires("path_writable", PathPort)
            .Build() with
            {
                ExecutionMode = ExecutionMode.Waitable,
                DisplayName = "Import Asset",
                Description =
                    "Imports a file that already exists on disk as a project asset with " +
                    "AssetDatabase.ImportAsset(path, ForceUpdate) and waits until the " +
                    "main asset at the path loads. The file must exist on disk before the " +
                    "step runs; a missing file and an import that stays unloadable fail " +
                    "with OW-UNITY-008 — the latter after three checks while the Editor " +
                    "is not updating, with the step's captured import errors attached. " +
                    "The path is normalized to '/' separators; a path containing '..' or " +
                    "spelled as a rooted or absolute path and a path outside the allowed " +
                    "paths fail with OW-PERM-002, and a destination inside a read-only " +
                    "package fails with OW-UNITY-010.",
                Category = "Asset",
                Tags = new[] { "asset", "import" },
                Keywords = new[] { "import", "asset", "file" },
                Examples = new[]
                {
                    new OperationExample(
                        "Import a dropped report file as an asset",
                        AssetOperationExamples.Import,
                        AssetOperationExamples.ImportFixtureManifest),
                },
            };
    }
}
