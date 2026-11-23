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
    /// <c>unity.asset.copy</c> (SPEC §I.3, §G7): duplicates the asset the
    /// <c>asset</c> Ref names at <c>destination</c>. Immediate, Undo None,
    /// <see cref="Idempotency.Reconcilable"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Paths.</b> The destination is a declared
    /// <c>unity.AssetPath</c> write port: judged by
    /// <see cref="AssetPathPolicy"/> — a <c>..</c> or rooted spelling is
    /// OW-PERM-002, a read-only package OW-UNITY-010 — and carrying the §G1
    /// <c>path_writable</c> requirement so the settings
    /// <c>allowedPaths</c> half is evaluated like every other write port.
    /// The source is only read, so a read-only package is a legal copy
    /// source; its parent folder must exist at the destination and the
    /// destination path must be completely free — a file, a folder, an
    /// orphan <c>.meta</c> or a remembered asset entry all count as
    /// occupied, and an existing path is never overwritten (OW-UNITY-005).
    /// </para>
    /// <para>
    /// <b>Reconciliation (§F5).</b> Preflight persists the evidence row —
    /// the source's guid and file content hash, the destination path, that
    /// the destination held nothing, and the Editor session id (evidence
    /// only; a file artifact re-identifies across a restart by its
    /// permanent keys, §F5 common rule 3). <see cref="Reconcile"/> decides
    /// only from that evidence: an asset at the destination with a new guid
    /// different from the source's and a content hash that matches the
    /// snapshot — the <c>.meta</c>'s guid line excluded, the one member a
    /// copy may differ in — is <see cref="ReconcileResult.Completed"/> with
    /// the AssetRef output restored; nothing at the destination is
    /// <see cref="ReconcileResult.NotApplied"/>; everything else — an
    /// un-imported artifact, the source's own guid at the destination, a
    /// content mismatch — is <see cref="ReconcileResult.Unknown"/>.
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class CopyAssetOperation : OperationBase
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.asset.copy";

        internal const string AssetPort = "asset";
        internal const string DestinationPort = "destination";

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
            // §D8 item 1: a pending port has no value to inspect in a Dry
            // Run, and a required port with no value yields no evidence to
            // record.
            if (ctx.IsPending(AssetPort) || ctx.IsPending(DestinationPort) ||
                !ctx.IsPresent(AssetPort) || !ctx.IsPresent(DestinationPort))
            {
                return base.Preflight(ctx);
            }

            var inputs = ReadInputs(ctx);
            if (!inputs.Ok)
            {
                return new PreflightResult(
                    inputs.Diagnostics, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }
            var plan = inputs.Value!;
            var diagnostic = CheckDestination(plan.Destination);
            if (diagnostic is not null)
            {
                return new PreflightResult(
                    new[] { diagnostic }, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }
            // SPEC §D0.3 item 3: whatever lands at the destination joins
            // the domain governing it — judged before any side effect.
            var guarded = GuardedCopy(plan);
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

            // The §F5 evidence row: the source guid and content hash, the
            // destination and its empty state — plus the session id as
            // evidence only (never the verdict's basis).
            var snapshot = new JObject
            {
                ["sourceGuid"] = plan.Guid,
                ["sourcePath"] = plan.SourcePath,
                ["sourceContentHash"] = hash,
                ["destination"] = plan.Destination,
                ["destinationExisted"] = false,
                ["editorSessionId"] = EditorSessionId.GetOrCreate(),
            };
            if (plan.LocalId.HasValue)
            {
                snapshot["sourceLocalId"] = plan.LocalId.Value;
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
            var inputs = ReadInputs(ctx);
            if (!inputs.Ok)
            {
                return StepResult.Failed(inputs.Diagnostics[0]);
            }
            var plan = inputs.Value!;
            var diagnostic = CheckDestination(plan.Destination);
            if (diagnostic is not null)
            {
                return StepResult.Failed(diagnostic);
            }
            // SPEC §D0.3 item 4: re-checked here, on the main thread Run
            // acceptance serializes on, so a Run admitted since Preflight
            // still blocks the copy — nothing is created when it refuses.
            var guarded = GuardedCopy(plan);
            if (guarded is not null)
            {
                return StepResult.Failed(guarded);
            }

            if (!AssetDatabase.CopyAsset(plan.SourcePath, plan.Destination))
            {
                return StepResult.Failed(CopyFailed());
            }
            var copy = plan.LocalId.HasValue
                ? SubAssetAt(plan.Destination, plan.LocalId.Value)
                : AssetDatabase.LoadMainAssetAtPath(plan.Destination);
            if (copy is null)
            {
                // CopyAsset succeeded but the product does not resolve —
                // the import-side failure, matching the file-not-imported
                // reading the create Operations apply.
                return StepResult.Failed(NotCreated());
            }
            var guid = AssetDatabase.AssetPathToGUID(
                plan.Destination, AssetPathToGUIDOptions.OnlyExistingAssets);

            ctx.Changes.Created(
                new ChangeTarget("asset", plan.Destination, guid),
                undoable: false);
            ctx.SetOutput(AssetPort, copy);
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
            if (!Member(obj, "sourceGuid", JTokenType.String, out var guidToken) ||
                !Member(obj, "sourcePath", JTokenType.String, out _) ||
                !Member(obj, "sourceContentHash", JTokenType.String, out var hashToken) ||
                !Member(obj, "destination", JTokenType.String, out var destinationToken) ||
                !Member(obj, "destinationExisted", JTokenType.Boolean, out var existedToken) ||
                !Member(obj, "editorSessionId", JTokenType.String, out _))
            {
                return Unknown("The Preflight snapshot is missing a required evidence member.");
            }
            var sourceGuid = (string)guidToken!;
            var expectedHash = (string)hashToken!;
            var destination = (string)destinationToken!;
            long? localId = obj["sourceLocalId"] is JValue { Type: JTokenType.Integer } id
                ? (long)id
                : null;

            if ((bool)existedToken!)
            {
                return Unknown(
                    "A pre-existing artifact occupies the destination; it cannot be attributed to the step.");
            }
            var physical = AssetArtifact.ToPhysicalPath(destination);
            if (physical is null)
            {
                return Unknown("The snapshot's destination can no longer be resolved.");
            }
            if (!AssetArtifact.Occupied(physical, destination))
            {
                // Nothing at the destination: the step provably did not
                // apply and re-execution can only copy, never overwrite.
                return new ReconcileResult.NotApplied();
            }
            var destinationGuid = AssetDatabase.AssetPathToGUID(
                destination, AssetPathToGUIDOptions.OnlyExistingAssets);
            if (destinationGuid.Length == 0 ||
                AssetDatabase.LoadMainAssetAtPath(destination) is null)
            {
                return Unknown(
                    "The artifact at the destination is not an imported asset.");
            }
            if (string.Equals(destinationGuid, sourceGuid, StringComparison.Ordinal))
            {
                return Unknown(
                    "The artifact at the destination carries the source's own guid.");
            }
            var copied = AssetArtifact.ContentHash(destination);
            if (!string.Equals(copied, expectedHash, StringComparison.Ordinal))
            {
                return Unknown(
                    "The artifact at the destination does not match the recorded content hash.");
            }
            var output = AssetRefAt(destination, localId);
            if (output is null)
            {
                return Unknown(
                    "The copied asset cannot be resolved to restore the declared output.");
            }
            return new ReconcileResult.Completed(
                new Dictionary<string, JsonValue>(StringComparer.Ordinal)
                {
                    [AssetPort] = output,
                });
        }

        // ── Inputs ───────────────────────────────────────────────────────

        private sealed class StepInputs
        {
            internal string SourcePath = string.Empty;
            internal string Guid = string.Empty;
            internal long? LocalId;
            internal string Destination = string.Empty;
        }

        // The validated inputs of a Preflight/Execute call — every port is
        // re-checked at step time because a value may arrive through a data
        // edge the static phases could not see (SPEC §D3, §E5). The asset
        // Ref resolves through §G2 (its own OW-UNITY-001/002/003
        // diagnostics pass through) and may name a read-only package asset
        // — a copy only reads the source; the destination is a §G7 item 1
        // write path.
        private static Result<StepInputs> ReadInputs(StepContext ctx)
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
            return Result<StepInputs>.Success(new StepInputs
            {
                SourcePath = sourcePath!,
                Guid = guid,
                LocalId = AssetDatabase.IsMainAsset(asset!) ? null : localId,
                Destination = destination.Value!,
            });
        }

        // The destination half of the §F5 row — the parent folder must
        // exist and the path must be completely free, because a copy never
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

        // The SPEC §D0.3 judgment of the copy (T-W4.2-06): the source is
        // only read, so the destination is the sole mutation surface — a
        // folder copy descends the incoming tree, a file copy classifies
        // the destination path and the source's content.
        private static Diagnostic? GuardedCopy(StepInputs plan) =>
            RunDefinitionGuard.CheckPath(
                plan.Destination, GuardedPathRole.Destination,
                isDirectory: AssetDatabase.IsValidFolder(plan.SourcePath),
                contentPath: plan.SourcePath);

        // The output object the AssetRef is published for: the main asset
        // at the destination, or the copied sub-asset at the recorded
        // localId (a copy preserves file-local identifiers).
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

        private static Diagnostic CopyFailed() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_005,
                "The Editor could not copy the asset to the destination.",
                port: DestinationPort);

        private static Diagnostic NotCreated() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_008,
                "The copied asset could not be resolved at the destination.",
                port: DestinationPort);

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Duplicates an asset at a new path; the destination is never overwritten.")
            .Ja("アセット複製",
                "アセットを複製して新しいパスに作成します。既存パスは上書きしません。",
                "アセット", "複製", "コピー")
            .Input(AssetPort, "unity.AssetRef<unity.Object>", required: true)
            .Input(DestinationPort, "unity.AssetPath", required: true)
            .Output(AssetPort, "unity.AssetRef<unity.Object>")
            .Effects(EffectNames.AssetCreate)
            .Undo(UndoSupport.None)
            .Idempotency(Idempotency.Reconcilable)
            // SPEC §G1 item 4 / §G7 item 1: the allowed-paths and
            // writability half of the asset path policy on the destination,
            // judged with the settings snapshot this Run's acceptance
            // validated (§H4.1, decision D-R6).
            .Requires("path_writable", DestinationPort)
            .Build() with
            {
                DisplayName = "Copy Asset",
                Description =
                    "Duplicates the referenced asset at the destination path through " +
                    "AssetDatabase.CopyAsset; the copy gets a fresh guid and keeps the " +
                    "source's content and import settings. The destination's parent " +
                    "folder must exist and the path must be free — an existing path " +
                    "fails with OW-UNITY-005 and is never overwritten, a path outside " +
                    "the allowed set fails with OW-PERM-002 and a destination in a " +
                    "read-only package fails with OW-UNITY-010; a read-only package is " +
                    "a legal copy source. The Operation is Reconcilable: after an " +
                    "interruption it completes only when the destination holds an " +
                    "imported asset whose guid differs from the source's and whose " +
                    "content matches the recorded hash (SPEC §F5).",
                Category = "Asset",
                Tags = new[] { "asset", "copy" },
                Keywords = new[] { "asset", "copy", "duplicate", "clone" },
                Examples = new[]
                {
                    new OperationExample(
                        "Copy the created material as a variant",
                        AssetOperationExamples.Copy),
                },
            };
    }
}
