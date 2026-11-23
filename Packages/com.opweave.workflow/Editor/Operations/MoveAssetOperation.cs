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
    /// <c>unity.asset.move</c> (SPEC §I.3, §G7 item 5): moves the asset the
    /// <c>asset</c> Ref names to <c>destination</c>, which is also how a
    /// rename is expressed. Immediate, Undo None,
    /// <see cref="Idempotency.Reconcilable"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Paths.</b> The move is a write on both ends. The destination is a
    /// declared <c>unity.AssetPath</c> write port: judged by
    /// <see cref="AssetPathPolicy"/> — a <c>..</c> or rooted spelling is
    /// OW-PERM-002, a read-only package OW-UNITY-010 — and carrying the §G1
    /// <c>path_writable</c> requirement so the settings
    /// <c>allowedPaths</c> half is evaluated like every other write port.
    /// The source is a resolved asset, not a path port — the same shape the
    /// §I.2 ref-targeted writers take — but leaving the project is a write
    /// too, so the resolved path is judged in the §G1 order like a write
    /// port: a source outside the Run's frozen <c>allowedPaths</c> fails
    /// OW-PERM-002 and a source inside a read-only package fails
    /// OW-UNITY-010, both before anything moves.
    /// </para>
    /// <para>
    /// <b>The move itself.</b> §G7 item 5: <c>ValidateMoveAsset</c> precedes
    /// <c>MoveAsset</c>, and the destination's parent folder must exist with
    /// the path completely free — a file, a folder, an orphan
    /// <c>.meta</c> or a remembered asset entry all count as occupied, and
    /// an existing path is never overwritten (OW-UNITY-005). A refusal is
    /// reported before any mutation.
    /// </para>
    /// <para>
    /// <b>Reconciliation (§F5).</b> Preflight persists the evidence row —
    /// the target's guid, the source and destination paths, that the
    /// destination held nothing, the target's content hash and the Editor
    /// session id (evidence only; a file artifact re-identifies across a
    /// restart by its permanent guid/path, §F5 common rule 3).
    /// <see cref="Reconcile"/> decides only from that evidence: the same
    /// guid at the destination, nothing at the source and a matching
    /// content hash is <see cref="ReconcileResult.Completed"/> with the
    /// AssetRef output restored; the same guid at the source, an empty
    /// destination and a matching content hash is
    /// <see cref="ReconcileResult.NotApplied"/>; everything else — the guid
    /// somewhere else or unresolvable, a different artifact at the
    /// destination or content that changed — is
    /// <see cref="ReconcileResult.Unknown"/>.
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class MoveAssetOperation : OperationBase
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.asset.move";

        internal const string AssetPort = "asset";
        internal const string DestinationPort = "destination";

        private static readonly JsonValue s_emptySnapshot =
            JsonValue.From(new JObject());

        // The allowedPaths of the settings snapshot the active Run's
        // acceptance validated (§H4.1, decision D-R6) — the same authority
        // the path_writable evaluator judges the destination by — applied
        // here to the resolved source path, which no declared port covers.
        private readonly Func<IReadOnlyList<string>?> _allowedPaths;

        /// <summary>Creates the Operation over the live asset database.</summary>
        public MoveAssetOperation()
            : this(CurrentRunAllowedPaths)
        {
        }

        /// <summary>
        /// The test seam over the Run's allowed paths — the same
        /// <see cref="EditorStateProbe"/> pattern the UnityAdapter uses, so
        /// the §G7 item 1 write bound on the resolved source path is
        /// provable without accepting a real Run.
        /// <paramref name="allowedPaths"/> answers <see langword="null"/>
        /// for "no active Run recorded a snapshot" — an unavailable frozen
        /// boundary fails closed: every resolved source is outside.
        /// </summary>
        internal MoveAssetOperation(Func<IReadOnlyList<string>?> allowedPaths)
        {
            _allowedPaths =
                allowedPaths ?? throw new ArgumentNullException(nameof(allowedPaths));
        }

        // The frozen snapshot of the Run currently executing, read through
        // the host accessor the requirement evaluator uses — never the
        // settings file, which a mid-Run edit could not be allowed to
        // loosen.
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
            // §D8 item 1: a pending port has no value to inspect in a Dry
            // Run, and a required port with no value yields no evidence to
            // record.
            if (ctx.IsPending(AssetPort) || ctx.IsPending(DestinationPort) ||
                !ctx.IsPresent(AssetPort) || !ctx.IsPresent(DestinationPort))
            {
                return base.Preflight(ctx);
            }

            var inputs = ReadInputs(ctx, _allowedPaths());
            if (!inputs.Ok)
            {
                return new PreflightResult(
                    inputs.Diagnostics, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }
            var plan = inputs.Value!;
            var diagnostic = CheckDestination(plan.Destination);
            if (diagnostic is null)
            {
                diagnostic = CheckMovable(plan.SourcePath, plan.Destination);
            }
            if (diagnostic is not null)
            {
                return new PreflightResult(
                    new[] { diagnostic }, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }
            // SPEC §D0.3 item 3: the source leaves its assemblies and the
            // destination's contents join the governing domain — both
            // judged before any side effect.
            var guarded = GuardedMove(plan);
            if (guarded is not null)
            {
                return new PreflightResult(
                    new[] { guarded }, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }
            var hash = AssetArtifact.ContentHash(plan.SourcePath);
            if (hash is null)
            {
                return new PreflightResult(
                    new[] { TargetMissing() },
                    Array.Empty<PlannedChange>(),
                    s_emptySnapshot);
            }

            // The §F5 evidence row: the target guid, source and destination
            // paths, the empty destination, the content hash — plus the
            // session id as evidence only (never the verdict's basis).
            var snapshot = new JObject
            {
                ["guid"] = plan.Guid,
                ["sourcePath"] = plan.SourcePath,
                ["destination"] = plan.Destination,
                ["destinationExisted"] = false,
                ["contentHash"] = hash,
                ["editorSessionId"] = EditorSessionId.GetOrCreate(),
            };
            if (plan.LocalId.HasValue)
            {
                snapshot["localId"] = plan.LocalId.Value;
            }
            return new PreflightResult(
                Array.Empty<Diagnostic>(),
                Array.Empty<PlannedChange>(),
                JsonValue.From(snapshot));
        }

        /// <inheritdoc/>
        public override StepResult Execute(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            var inputs = ReadInputs(ctx, _allowedPaths());
            if (!inputs.Ok)
            {
                return StepResult.Failed(inputs.Diagnostics[0]);
            }
            var plan = inputs.Value!;
            var diagnostic = CheckDestination(plan.Destination);
            if (diagnostic is null)
            {
                diagnostic = CheckMovable(plan.SourcePath, plan.Destination);
            }
            if (diagnostic is not null)
            {
                return StepResult.Failed(diagnostic);
            }
            // SPEC §D0.3 item 4: re-checked here, on the main thread Run
            // acceptance serializes on, so a Run admitted since Preflight
            // still blocks the move — nothing is moved when it refuses.
            var guarded = GuardedMove(plan);
            if (guarded is not null)
            {
                return StepResult.Failed(guarded);
            }

            var failure = AssetDatabase.MoveAsset(plan.SourcePath, plan.Destination);
            if (!string.IsNullOrEmpty(failure))
            {
                return StepResult.Failed(MoveFailed());
            }

            ctx.Changes.Moved(
                new ChangeTarget("asset", plan.Destination, plan.Guid),
                undoable: false);
            ctx.SetOutput(AssetPort, plan.Asset!);
            return StepResult.Succeeded();
        }

        /// <inheritdoc/>
        public override ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }
            if (snapshot.ToToken() is not JObject obj)
            {
                return Unknown("The Preflight snapshot is not an object.");
            }
            if (!Member(obj, "guid", JTokenType.String, out var guidToken) ||
                !Member(obj, "sourcePath", JTokenType.String, out var sourceToken) ||
                !Member(obj, "destination", JTokenType.String, out var destinationToken) ||
                !Member(obj, "destinationExisted", JTokenType.Boolean, out var existedToken) ||
                !Member(obj, "contentHash", JTokenType.String, out var hashToken) ||
                !Member(obj, "editorSessionId", JTokenType.String, out _))
            {
                return Unknown("The Preflight snapshot is missing a required evidence member.");
            }
            var guid = (string)guidToken!;
            var sourcePath = (string)sourceToken!;
            var destination = (string)destinationToken!;
            var expectedHash = (string)hashToken!;
            long? localId = obj["localId"] is JValue { Type: JTokenType.Integer } id
                ? (long)id
                : null;

            if ((bool)existedToken!)
            {
                return Unknown(
                    "A pre-existing artifact occupies the destination; it cannot be attributed to the step.");
            }
            var sourcePhysical = AssetArtifact.ToPhysicalPath(sourcePath);
            var destinationPhysical = AssetArtifact.ToPhysicalPath(destination);
            if (sourcePhysical is null || destinationPhysical is null)
            {
                return Unknown("The snapshot's paths can no longer be resolved.");
            }

            // GUIDToAssetPath may keep answering with a stale path; only a
            // guid backed by a live asset counts.
            var guidPath = AssetArtifact.LiveAssetPath(guid);
            if (string.Equals(guidPath, destination, StringComparison.Ordinal))
            {
                if (AssetArtifact.Occupied(sourcePhysical, sourcePath))
                {
                    return Unknown(
                        "An artifact occupies the recorded source path.");
                }
                var moved = AssetArtifact.ContentHash(destination);
                if (!string.Equals(moved, expectedHash, StringComparison.Ordinal))
                {
                    return Unknown(
                        "The artifact at the destination does not match the recorded content hash.");
                }
                var output = AssetRefAt(destination, localId);
                if (output is null)
                {
                    return Unknown(
                        "The moved asset cannot be resolved to restore the declared output.");
                }
                return new ReconcileResult.Completed(
                    new Dictionary<string, JsonValue>(StringComparer.Ordinal)
                    {
                        [AssetPort] = output,
                    });
            }
            if (string.Equals(guidPath, sourcePath, StringComparison.Ordinal))
            {
                if (AssetArtifact.Occupied(destinationPhysical, destination))
                {
                    return Unknown(
                        "An artifact occupies the destination while the target still sits at the source.");
                }
                var source = AssetArtifact.ContentHash(sourcePath);
                if (!string.Equals(source, expectedHash, StringComparison.Ordinal))
                {
                    return Unknown(
                        "The artifact at the source does not match the recorded content hash.");
                }
                return new ReconcileResult.NotApplied();
            }
            return Unknown(guidPath is null
                ? "The recorded guid no longer resolves to a project asset."
                : "The recorded guid resolves at a different path.");
        }

        // ── Inputs ───────────────────────────────────────────────────────

        private sealed class StepInputs
        {
            internal UnityEngine.Object? Asset;
            internal string SourcePath = string.Empty;
            internal string Guid = string.Empty;
            internal long? LocalId;
            internal string Destination = string.Empty;
        }

        // The validated inputs of a Preflight/Execute call — every port is
        // re-checked at step time because a value may arrive through a data
        // edge the static phases could not see (SPEC §D3, §E5). The asset
        // Ref resolves through §G2 (its own OW-UNITY-001/002/003
        // diagnostics pass through); the destination is a §G7 item 1 write
        // path; and the source is a write at its own path — judged in the
        // §G1 order, outside the Run's allowed paths OW-PERM-002, inside a
        // read-only package OW-UNITY-010, before anything moves.
        private static Result<StepInputs> ReadInputs(
            StepContext ctx, IReadOnlyList<string>? allowedPaths)
        {
            var diagnostics = new List<Diagnostic>();

            UnityEngine.Object? asset = null;
            try
            {
                asset = ctx.Get<UnityEngine.Object>(AssetPort);
            }
            catch (StepContextException failure)
            {
                diagnostics.AddRange(failure.Diagnostics);
            }

            var destination = AssetPathPolicy.CheckWritePath(
                ctx.Get<string>(DestinationPort), DestinationPort);
            if (!destination.Ok)
            {
                diagnostics.AddRange(destination.Diagnostics);
            }
            if (diagnostics.Count != 0)
            {
                return Result<StepInputs>.Failure(diagnostics);
            }

            var sourcePath = asset is null
                ? null
                : AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(sourcePath) ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    asset!, out var guid, out var localId) ||
                guid.Length == 0)
            {
                return Result<StepInputs>.Failure(TargetMissing());
            }
            if (!AssetPathPolicy.IsInsideAllowedPaths(sourcePath!, allowedPaths))
            {
                return Result<StepInputs>.Failure(
                    AssetPathPolicy.OutsideAllowedPaths(AssetPort));
            }
            if (AssetPathPolicy.IsInImmutablePackage(sourcePath!))
            {
                return Result<StepInputs>.Failure(
                    AssetPathPolicy.ReadOnlyPath(AssetPort));
            }
            return Result<StepInputs>.Success(new StepInputs
            {
                Asset = asset,
                SourcePath = sourcePath!,
                Guid = guid,
                LocalId = AssetDatabase.IsMainAsset(asset!) ? null : localId,
                Destination = destination.Value!,
            });
        }

        // The destination half of the §F5 row — the parent folder must
        // exist and the path must be completely free, because a move never
        // writes over an existing artifact (and the snapshot later answers
        // "was anything there").
        private static Diagnostic? CheckDestination(string destination)
        {
            var parent = AssetArtifact.ParentFolder(destination);
            var physical = AssetArtifact.ToPhysicalPath(destination);
            if (physical is null || parent is null ||
                !AssetDatabase.IsValidFolder(parent))
            {
                return FolderMissing();
            }
            return AssetArtifact.Occupied(physical, destination)
                ? DestinationOccupied()
                : null;
        }

        // §G7 item 5: ValidateMoveAsset always precedes MoveAsset — a move
        // Unity itself refuses is never attempted.
        private static Diagnostic? CheckMovable(string source, string destination) =>
            string.IsNullOrEmpty(AssetDatabase.ValidateMoveAsset(source, destination))
                ? null
                : MoveRefused();

        // The SPEC §D0.3 judgment of the move (T-W4.2-06): the source's
        // files leave the assemblies that compile them (Source), and
        // whatever lands at the destination joins the domain governing it
        // (Destination) — the guard's directory walk descends when the
        // source is a folder, conservatively refusing a boundary move it
        // cannot prove unrelated.
        private static Diagnostic? GuardedMove(StepInputs plan)
        {
            var directory = AssetDatabase.IsValidFolder(plan.SourcePath);
            return RunDefinitionGuard.CheckPath(
                    plan.SourcePath, GuardedPathRole.Source,
                    isDirectory: directory) ??
                RunDefinitionGuard.CheckPath(
                    plan.Destination, GuardedPathRole.Destination,
                    isDirectory: directory, contentPath: plan.SourcePath);
        }

        // The output object the AssetRef is published for: the main asset
        // at the destination, or the recorded sub-asset localId inside it.
        private static JsonValue? AssetRefAt(string path, long? localId)
        {
            var obj = localId.HasValue
                ? SubAssetAt(path, localId.Value)
                : AssetDatabase.LoadMainAssetAtPath(path);
            if (obj is null)
            {
                return null;
            }
            try
            {
                return new RefResolver().ToRef(obj, "unity.AssetRef<unity.Object>");
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static UnityEngine.Object? SubAssetAt(string path, long localId)
        {
            foreach (var candidate in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (candidate is not null &&
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                        candidate, out _, out var id) &&
                    id == localId)
                {
                    return candidate;
                }
            }
            return null;
        }

        // ── Diagnostics ──────────────────────────────────────────────────

        private static bool Member(
            JObject obj, string name, JTokenType type, out JToken? value)
        {
            value = obj[name];
            return value is not null && value.Type == type;
        }

        private static ReconcileResult Unknown(string reason) =>
            new ReconcileResult.Unknown(reason);

        private static Diagnostic TargetMissing() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_001,
                "The referenced asset is not a persistent project asset.",
                port: AssetPort,
                hint: "Point the Ref at an asset that exists on disk.");

        private static Diagnostic FolderMissing() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_005,
                "The destination folder does not exist.",
                port: DestinationPort,
                hint: "Create the folder first, or choose a folder that exists.");

        private static Diagnostic DestinationOccupied() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_005,
                "An artifact already occupies the destination and is never overwritten.",
                port: DestinationPort,
                hint: "Choose a destination path that is free.");

        private static Diagnostic MoveRefused() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_005,
                "The Editor refused the requested move.",
                port: DestinationPort,
                hint: "The source and destination must name a valid move for this asset.");

        private static Diagnostic MoveFailed() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_005,
                "The Editor could not move the asset to the destination.",
                port: DestinationPort);

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Moves or renames an asset; the destination is validated and never overwritten.")
            .Ja("アセット移動",
                "アセットを移動・名前変更します。移動先を事前検証し、既存パスは上書きしません。",
                "アセット", "移動", "名前変更")
            .Input(AssetPort, "unity.AssetRef<unity.Object>", required: true)
            .Input(DestinationPort, "unity.AssetPath", required: true)
            .Output(AssetPort, "unity.AssetRef<unity.Object>")
            .Effects(EffectNames.AssetMove)
            .Undo(UndoSupport.None)
            .Idempotency(Idempotency.Reconcilable)
            // SPEC §G1 item 4 / §G7 item 1: the allowed-paths and
            // writability half of the asset path policy on the destination,
            // judged with the settings snapshot this Run's acceptance
            // validated (§H4.1, decision D-R6).
            .Requires("path_writable", DestinationPort)
            .Build() with
            {
                DisplayName = "Move Asset",
                Description =
                    "Moves the referenced asset to the destination path — the rename " +
                    "Operation as well, since renaming is moving to a new name. The " +
                    "destination's parent folder must exist and the path must be free; " +
                    "a destination outside the allowed set and a source outside the " +
                    "Run's allowed paths fail with OW-PERM-002, a read-only " +
                    "package on either side fails with OW-UNITY-010, and the move is " +
                    "validated with ValidateMoveAsset before anything mutates " +
                    "(OW-UNITY-005). The Operation is Reconcilable: after an " +
                    "interruption it completes only when the recorded guid sits at the " +
                    "destination with the recorded content and nothing remains at the " +
                    "source (SPEC §F5).",
                Category = "Asset",
                Tags = new[] { "asset", "move" },
                Keywords = new[] { "asset", "move", "rename" },
                Examples = new[]
                {
                    new OperationExample(
                        "Rename the created material",
                        AssetOperationExamples.Move),
                },
            };
    }
}
