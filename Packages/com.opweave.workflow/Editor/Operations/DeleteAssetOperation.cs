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
    /// <c>unity.asset.delete</c> (SPEC §I.3, §G7 item 5): deletes the asset
    /// the <c>asset</c> Ref names — through
    /// <c>AssetDatabase.MoveAssetToTrash</c> by default
    /// (<c>mode&nbsp;=&nbsp;"trash"</c>), and only
    /// <c>mode&nbsp;=&nbsp;"permanent"</c> calls
    /// <c>AssetDatabase.DeleteAsset</c>. Destructive, Immediate, Undo None,
    /// <see cref="Idempotency.Reconcilable"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Target.</b> The target is a resolved asset, not a path port — but
    /// deleting it is a write inside its container, so the resolved path
    /// is judged in the §G1 order like a write port: an asset outside the
    /// Run's frozen <c>allowedPaths</c> is refused OW-PERM-002 and one in
    /// a read-only package OW-UNITY-010, both before anything is deleted
    /// (SPEC §G7 item 1: <c>Packages/</c> is readable only).
    /// </para>
    /// <para>
    /// <b>Reconciliation (§F5).</b> Preflight persists the evidence row —
    /// the target's guid, path, content hash, the <c>mode</c>, the
    /// neighbours of the target inside its parent folder (the "other
    /// assets" a Completed verdict must prove are still there — the
    /// bounded scope the operation can affect, mirroring the parent
    /// children list <c>unity.gameobject.delete</c> records) and the Editor
    /// session id (evidence only; a file artifact re-identifies across a
    /// restart by its permanent guid/path, §F5 common rule 3).
    /// <see cref="Reconcile"/> decides only from that evidence: the guid
    /// unresolvable, nothing at the path, and every recorded sibling still
    /// in place with its recorded guid is
    /// <see cref="ReconcileResult.Completed"/>; the same guid at the same
    /// path with the same content hash is
    /// <see cref="ReconcileResult.NotApplied"/>; everything else — the guid
    /// at a different path, a changed content hash, something else at the
    /// path, a sibling gone — is <see cref="ReconcileResult.Unknown"/>.
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class DeleteAssetOperation : OperationBase
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.asset.delete";

        internal const string AssetPort = "asset";
        internal const string ModePort = "mode";

        /// <summary>The <c>mode</c> enum's default value (SPEC §I.3).</summary>
        internal const string TrashMode = "trash";

        /// <summary>The <c>mode</c> enum's permanent value (SPEC §I.3).</summary>
        internal const string PermanentMode = "permanent";

        private static readonly JsonValue s_emptySnapshot =
            JsonValue.From(new JObject());

        // The allowedPaths of the settings snapshot the active Run's
        // acceptance validated (§H4.1, decision D-R6) — the same authority
        // the path_writable evaluator judges a declared write port by —
        // applied here to the resolved target path, which no declared
        // port covers.
        private readonly Func<IReadOnlyList<string>?> _allowedPaths;

        /// <summary>Creates the Operation over the live asset database.</summary>
        public DeleteAssetOperation()
            : this(CurrentRunAllowedPaths)
        {
        }

        /// <summary>
        /// The test seam over the Run's allowed paths — the same
        /// <see cref="EditorStateProbe"/> pattern the UnityAdapter uses, so
        /// the §G7 item 1 write bound on the resolved target path is
        /// provable without accepting a real Run.
        /// <paramref name="allowedPaths"/> answers <see langword="null"/>
        /// for "no active Run recorded a snapshot" — an unavailable frozen
        /// boundary fails closed: every resolved target is outside.
        /// </summary>
        internal DeleteAssetOperation(Func<IReadOnlyList<string>?> allowedPaths)
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
            if (ctx.IsPending(AssetPort) || ctx.IsPending(ModePort) ||
                !ctx.IsPresent(AssetPort))
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
            // SPEC §D0.3 item 3: deleting the target removes its file(s)
            // from the assemblies that compile them — a folder deletes its
            // whole subtree — judged before any side effect.
            var guarded = RunDefinitionGuard.CheckPath(
                plan.Path, GuardedPathRole.Source);
            if (guarded is not null)
            {
                return new PreflightResult(
                    new[] { guarded }, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }
            var hash = AssetArtifact.ContentHash(plan.Path);
            var siblings = AssetArtifact.SiblingGuids(plan.Path);
            if (hash is null || siblings is null)
            {
                return new PreflightResult(
                    new[] { TargetMissing() },
                    Array.Empty<PlannedChange>(),
                    s_emptySnapshot);
            }

            // The §F5 evidence row: guid, path, content hash, mode and the
            // sibling list — plus the session id as evidence only (never
            // the verdict's basis).
            var snapshot = new JObject
            {
                ["guid"] = plan.Guid,
                ["path"] = plan.Path,
                ["contentHash"] = hash,
                ["mode"] = plan.Mode,
                ["editorSessionId"] = EditorSessionId.GetOrCreate(),
            };
            var siblingMap = new JObject();
            foreach (var pair in siblings)
            {
                siblingMap[pair.Key] = pair.Value;
            }
            snapshot["siblings"] = siblingMap;
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
            // SPEC §D0.3 item 4: re-checked here, on the main thread Run
            // acceptance serializes on, so a Run admitted since Preflight
            // still blocks the deletion — nothing is deleted when it refuses.
            var guarded = RunDefinitionGuard.CheckPath(
                plan.Path, GuardedPathRole.Source);
            if (guarded is not null)
            {
                return StepResult.Failed(guarded);
            }

            var deleted = plan.Mode == PermanentMode
                ? AssetDatabase.DeleteAsset(plan.Path)
                : AssetDatabase.MoveAssetToTrash(plan.Path);
            if (!deleted)
            {
                return StepResult.Failed(DeleteFailed());
            }

            ctx.Changes.Deleted(
                new ChangeTarget("asset", plan.Path, plan.Guid),
                undoable: false);
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
                !Member(obj, "path", JTokenType.String, out var pathToken) ||
                !Member(obj, "contentHash", JTokenType.String, out var hashToken) ||
                !Member(obj, "mode", JTokenType.String, out _) ||
                !Member(obj, "editorSessionId", JTokenType.String, out _) ||
                obj["siblings"] is not JObject siblings)
            {
                return Unknown("The Preflight snapshot is missing a required evidence member.");
            }
            var guid = (string)guidToken!;
            var path = (string)pathToken!;
            var expectedHash = (string)hashToken!;

            // GUIDToAssetPath may keep answering with the deleted asset's
            // former path; only a guid backed by a live asset counts.
            var guidPath = AssetArtifact.LiveAssetPath(guid);
            if (string.Equals(guidPath, path, StringComparison.Ordinal))
            {
                var content = AssetArtifact.ContentHash(path);
                return string.Equals(content, expectedHash, StringComparison.Ordinal)
                    ? (ReconcileResult)new ReconcileResult.NotApplied()
                    : Unknown(
                        "The artifact at the recorded path does not match the recorded content hash.");
            }
            if (guidPath is not null)
            {
                return Unknown(
                    "The recorded guid resolves at a different path.");
            }
            var physical = AssetArtifact.ToPhysicalPath(path);
            if (physical is null || AssetArtifact.Occupied(physical, path))
            {
                return Unknown(
                    "An artifact occupies the recorded path while the guid is unresolvable.");
            }
            if (!SiblingsIntact(path, siblings))
            {
                return Unknown(
                    "An asset beside the recorded target is missing or changed.");
            }
            return new ReconcileResult.Completed(
                new Dictionary<string, JsonValue>(0));
        }

        // ── Inputs ───────────────────────────────────────────────────────

        private sealed class StepInputs
        {
            internal string Path = string.Empty;
            internal string Guid = string.Empty;
            internal string Mode = TrashMode;
        }

        // The validated inputs of a Preflight/Execute call — every port is
        // re-checked at step time because a value may arrive through a data
        // edge the static phases could not see (SPEC §D3, §E5). The asset
        // Ref resolves through §G2 (its own OW-UNITY-001/002/003
        // diagnostics pass through); the target's own path is judged like
        // a write in the §G1 order — outside the Run's allowed paths
        // OW-PERM-002, inside a read-only package OW-UNITY-010; and mode is
        // the §I.3 enum, defaulting to trash when the port is absent.
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

            var mode = ctx.IsPresent(ModePort)
                ? ctx.Get<string>(ModePort)
                : TrashMode;
            if (mode != TrashMode && mode != PermanentMode)
            {
                diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_OP_006,
                    $"Parameter '{ModePort}' must be '{TrashMode}' or '{PermanentMode}'.",
                    port: ModePort));
            }
            if (diagnostics.Count != 0)
            {
                return Result<StepInputs>.Failure(diagnostics);
            }

            var path = asset is null ? null : AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(path) ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    asset!, out var guid, out _) ||
                guid.Length == 0)
            {
                return Result<StepInputs>.Failure(TargetMissing());
            }
            if (!AssetPathPolicy.IsInsideAllowedPaths(path!, allowedPaths))
            {
                return Result<StepInputs>.Failure(
                    AssetPathPolicy.OutsideAllowedPaths(AssetPort));
            }
            if (AssetPathPolicy.IsInImmutablePackage(path!))
            {
                return Result<StepInputs>.Failure(
                    AssetPathPolicy.ReadOnlyPath(AssetPort));
            }
            return Result<StepInputs>.Success(new StepInputs
            {
                Path = path!,
                Guid = guid,
                Mode = mode!,
            });
        }

        // The Completed side of the §F5 row's "other assets are not gone":
        // every neighbour the snapshot recorded is still a file-system
        // entry beside the target's path and — when the snapshot recorded
        // its guid — still imports as the same asset. The parent folder
        // itself is part of that state: it may not have vanished either.
        private static bool SiblingsIntact(string path, JObject recorded)
        {
            var parent = AssetArtifact.ParentFolder(path);
            var physical = parent is null ? null : AssetArtifact.ToPhysicalPath(parent);
            if (physical is null || !Directory.Exists(physical))
            {
                return recorded.Count == 0 && physical is null;
            }
            foreach (var pair in recorded)
            {
                var name = pair.Key;
                var expectedGuid = pair.Value is { Type: JTokenType.String } token
                    ? (string?)token
                    : null;
                if (expectedGuid is null)
                {
                    return false;
                }
                var childPhysical = Path.Combine(physical, name);
                if (!File.Exists(childPhysical) &&
                    !Directory.Exists(childPhysical) &&
                    !File.Exists(childPhysical + ".meta"))
                {
                    return false;
                }
                if (expectedGuid.Length != 0 &&
                    !string.Equals(
                        AssetDatabase.AssetPathToGUID(
                            parent! + "/" + name,
                            AssetPathToGUIDOptions.OnlyExistingAssets),
                        expectedGuid,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
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

        private static Diagnostic DeleteFailed() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_005,
                "The Editor could not delete the asset.",
                port: AssetPort);

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Deletes an asset — to the trash by default, permanently on request. Destructive.")
            .Ja("アセット削除",
                "アセットを削除します。既定はゴミ箱へ移動、permanent で完全削除です。破壊的操作。",
                "アセット", "削除", "ゴミ箱")
            .Input(AssetPort, "unity.AssetRef<unity.Object>", required: true)
            .Input(ModePort, "core.String", required: false,
                constraints: c => c.Enum(TrashMode, PermanentMode),
                @default: JsonValue.From(new JValue(TrashMode)))
            .Effects(EffectNames.AssetDelete)
            .Undo(UndoSupport.None)
            .Idempotency(Idempotency.Reconcilable)
            .Build() with
            {
                DisplayName = "Delete Asset",
                Description =
                    "Deletes the referenced asset: the default 'trash' mode moves it " +
                    "to the trash through AssetDatabase.MoveAssetToTrash, and only " +
                    "the 'permanent' mode calls AssetDatabase.DeleteAsset. An asset " +
                    "outside the Run's allowed paths fails with OW-PERM-002 and one " +
                    "inside a read-only package fails with OW-UNITY-010. This is a " +
                    "destructive Operation. It is Reconcilable: after an interruption " +
                    "it completes only when the recorded guid no longer resolves, " +
                    "nothing occupies the recorded path and every other recorded " +
                    "asset beside it is still present (SPEC §F5).",
                Category = "Asset",
                Tags = new[] { "asset", "delete" },
                Keywords = new[] { "asset", "delete", "remove", "trash" },
                Destructive = true,
                Examples = new[]
                {
                    new OperationExample(
                        "Delete the material created by an earlier step",
                        AssetOperationExamples.Delete),
                },
            };
    }
}
