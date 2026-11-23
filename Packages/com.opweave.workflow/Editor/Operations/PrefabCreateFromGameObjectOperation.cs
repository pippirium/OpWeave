#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;

namespace OpWeave.Operations
{
    /// <summary>
    /// <c>unity.prefab.create_from_gameobject</c> (SPEC §I.2, §G6 item 1):
    /// saves the scene GameObject named by <c>source</c> as a Prefab asset
    /// at <c>path</c> — <see cref="PrefabUtility.SaveAsPrefabAssetAndConnect(GameObject, string, InteractionMode)"/>
    /// when <c>connect</c> (default true) so the source becomes the
    /// outermost instance of the new Prefab,
    /// <see cref="PrefabUtility.SaveAsPrefabAsset(GameObject, string)"/>
    /// otherwise. An occupied path refuses with OW-UNITY-005 unless
    /// <c>overwrite</c> is true, which adds the <c>asset.modify</c> effect
    /// and rewrites an existing Prefab asset only. Immediate, Undo
    /// <see cref="UndoSupport.Partial"/> (the asset file is not undoable,
    /// the scene connection is), <see cref="Idempotency.Reconcilable"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The §F5 evidence row: Preflight records the normalized path and the
    /// state before the step ran (nothing; with <c>overwrite</c> the
    /// existing guid and content fingerprint), the source GameObject's Ref
    /// identity and its Prefab state, the expected content fingerprint
    /// computed read-only from the source content (SPEC §G6.1), the
    /// connect flag and the Editor session id. <see cref="Reconcile"/>
    /// returns Completed only when the artifact's guid is consistent with
    /// the recorded pre-state (new path → a new asset; overwrite → the
    /// same guid), its content fingerprint equals the recorded expectation,
    /// and — connect only, in the same session — the source is the
    /// outermost instance of that Prefab; NotApplied only when the path
    /// state still equals the snapshot and the source is provably not
    /// connected; everything else is Unknown (NeedsReview). Neither
    /// verdict duplicates or deletes an artifact.
    /// </para>
    /// <para>
    /// Local, reversible choices: <c>overwrite</c> may only rewrite an
    /// existing <em>Prefab</em> asset — a directory, an orphan .meta or a
    /// foreign asset type still refuses with OW-UNITY-005; the Undo flag
    /// on the scene-object change record stays honest about Unity's own
    /// undo of the connect; the expected fingerprint is computed by
    /// <see cref="PrefabContentFingerprint.ComputeForSave"/>, which
    /// normalizes the root name to the destination file stem.
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class PrefabCreateFromGameObjectOperation
        : OperationBase, IEffectResolver
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.prefab.create_from_gameobject";

        internal const string SourcePort = "source";
        internal const string PathPort = "path";
        internal const string ConnectPort = "connect";
        internal const string OverwritePort = "overwrite";
        internal const string PrefabPort = "prefab";
        internal const string InstancePort = "instance";

        private static readonly JsonValue s_emptySnapshot =
            JsonValue.From(new JObject());
        private static readonly JsonValue s_defaultConnect =
            JsonValue.From(new JValue(true));
        private static readonly JsonValue s_defaultOverwrite =
            JsonValue.From(new JValue(false));

        /// <inheritdoc/>
        public override OperationDescriptor Descriptor { get; } = BuildDescriptor();

        /// <inheritdoc/>
        public override PreflightResult Preflight(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            // §D8 item 1: an edge-fed port has no value to inspect in a Dry
            // Run, and a missing required port yields no evidence to record.
            if (ctx.IsPending(SourcePort) || ctx.IsPending(PathPort) ||
                ctx.IsPending(ConnectPort) || ctx.IsPending(OverwritePort) ||
                !ctx.IsPresent(SourcePort) || !ctx.IsPresent(PathPort))
            {
                return base.Preflight(ctx);
            }
            var inputs = ReadInputs(ctx, out var source);
            if (!inputs.Ok)
            {
                return new PreflightResult(
                    inputs.Diagnostics, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }
            var plan = inputs.Value!;
            var destination = PrefabCreateEvidence.CheckDestination(
                plan.Path, PathPort, plan.Overwrite);
            if (destination is not null)
            {
                return new PreflightResult(
                    new[] { destination }, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }

            // The read-only expected fingerprint of the saved content
            // (§G6.1 item 1): an uncomputable one is recorded with its
            // reason, not hidden — the save stays valid, its Completed
            // cannot be proven by fingerprint.
            var expected = PrefabCreateEvidence.FingerprintEvidence(
                PrefabContentFingerprint.ComputeForSave(source!, plan.Path),
                null, plan.Path);
            var before = PrefabCreateEvidence.PathBeforeState(plan.Path);
            var snapshot = new JObject
            {
                ["path"] = plan.Path,
                ["pathExisted"] =
                    !((bool?)before["absent"] ?? false),
                ["connect"] = plan.Connect,
                ["overwrite"] = plan.Overwrite,
                ["before"] = before,
                ["expected"] = expected.ToToken(),
                ["source"] = GameObjectEvidence.RefOf(source!),
                ["sourcePrefabState"] = PrefabCreateEvidence.SourcePrefabState(source!),
                ["editorSessionId"] = EditorSessionId.GetOrCreate(),
            };
            return new PreflightResult(
                Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(),
                JsonValue.From(snapshot));
        }

        /// <inheritdoc/>
        public override StepResult Execute(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            var inputs = ReadInputs(ctx, out var source);
            if (!inputs.Ok)
            {
                return StepResult.Failed(inputs.Diagnostics[0]);
            }
            var plan = inputs.Value!;
            var destination = PrefabCreateEvidence.CheckDestination(
                plan.Path, PathPort, plan.Overwrite);
            if (destination is not null)
            {
                return StepResult.Failed(destination);
            }
            var existed = AssetArtifact.Occupied(
                AssetArtifact.ToPhysicalPath(plan.Path)!, plan.Path);

            GameObject? saved;
            try
            {
                saved = plan.Connect
                    ? PrefabUtility.SaveAsPrefabAssetAndConnect(
                        source!, plan.Path, InteractionMode.AutomatedAction)
                    : PrefabUtility.SaveAsPrefabAsset(source!, plan.Path);
            }
            catch (Exception e)
            {
                return StepResult.Failed(PrefabCreateEvidence.WriteFailed(
                    PathPort, "the write failed with " + e.GetType().Name));
            }
            if (saved == null)
            {
                return StepResult.Failed(PrefabCreateEvidence.WriteFailed(
                    PathPort, "the asset could not be written"));
            }

            var guid = AssetDatabase.AssetPathToGUID(plan.Path);
            if (existed)
            {
                ctx.Changes.Saved(
                    new ChangeTarget("asset", plan.Path, guid), undoable: false);
            }
            else
            {
                ctx.Changes.Created(
                    new ChangeTarget("asset", plan.Path, guid), undoable: false);
            }
            if (plan.Connect)
            {
                // The scene half of the step: the source became the
                // outermost instance of the new Prefab — the undoable part
                // Undo Partial declares (the asset file is not undoable).
                ctx.Changes.Modified(
                    new ChangeTarget(
                        "gameObject",
                        RefHierarchyPath.For(source!),
                        null,
                        GlobalObjectId.GetGlobalObjectIdSlow(source!).ToString()),
                    undoable: true);
            }

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(plan.Path);
            if (asset == null)
            {
                return StepResult.Failed(PrefabCreateEvidence.WriteFailed(
                    PathPort, "the saved asset could not be loaded after the save"));
            }
            ctx.SetOutput(PrefabPort, asset);
            if (plan.Connect)
            {
                ctx.SetOutput(InstancePort, source!);
            }
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
            if (!Member(obj, "path", JTokenType.String, out var pathToken) ||
                !Member(obj, "pathExisted", JTokenType.Boolean, out var existedToken) ||
                !Member(obj, "connect", JTokenType.Boolean, out var connectToken) ||
                !Member(obj, "overwrite", JTokenType.Boolean, out var overwriteToken) ||
                obj["before"] is not JObject before ||
                obj["expected"] is not { } expected ||
                obj["source"] is not JObject sourceRef ||
                obj["sourcePrefabState"] is not JObject ||
                !Member(obj, "editorSessionId", JTokenType.String, out var sessionToken))
            {
                return Unknown(
                    "The Preflight snapshot is missing a required evidence member.");
            }
            var path = (string)pathToken!;
            var pathExisted = (bool)existedToken!;
            var connect = (bool)connectToken!;
            var overwrite = (bool)overwriteToken!;

            var sourceResolved = TryResolveSource(sourceRef, out var source);

            if (!PrefabCreateEvidence.OccupiedNow(path))
            {
                // Nothing at the path: NotApplied needs the snapshot's own
                // empty pre-state and a source provably not connected.
                if (pathExisted)
                {
                    return Unknown(
                        "The snapshot recorded an existing artifact at the path, which is now gone.");
                }
                if (!sourceResolved)
                {
                    return Unknown(
                        "The source GameObject cannot be resolved, so its unconnected state cannot be proven.");
                }
                return PrefabCreateEvidence.IsOutermostInstanceOf(source, path)
                    ? Unknown(
                        "The source is connected to a Prefab at the path the snapshot recorded empty.")
                    : (ReconcileResult)new ReconcileResult.NotApplied();
            }

            // Something occupies the path: it must be an imported Prefab
            // asset whose guid is consistent with the recorded pre-state.
            var physical = AssetArtifact.ToPhysicalPath(path);
            var guid = AssetDatabase.AssetPathToGUID(
                path, AssetPathToGUIDOptions.OnlyExistingAssets);
            var isPrefabFile = physical is not null && File.Exists(physical) &&
                !Directory.Exists(physical) && guid.Length != 0 &&
                AssetDatabase.LoadAssetAtPath<GameObject>(path) is { } loaded &&
                PrefabUtility.GetPrefabAssetType(loaded) != PrefabAssetType.NotAPrefab;
            if (!isPrefabFile)
            {
                return Unknown(
                    "The artifact at the target path is not an imported Prefab asset.");
            }

            if (pathExisted)
            {
                var recordedGuid = before["guid"] as JValue;
                if (!string.Equals(
                        recordedGuid?.Value as string, guid, StringComparison.Ordinal))
                {
                    return Unknown(
                        "The artifact's guid differs from the recorded pre-existing asset.");
                }
                // The recorded pre-state fingerprint decides "unchanged":
                // unchanged + unconnected source is the NotApplied half of
                // the row; changed must equal the expected fingerprint to
                // be this step's overwrite product.
                var beforeFingerprint = (before["fingerprint"] as JValue)?.Value as string;
                var current = CurrentFingerprint(path);
                if (current is null)
                {
                    return Unknown(
                        "The artifact's content fingerprint could not be computed.");
                }
                if (string.Equals(current, beforeFingerprint, StringComparison.Ordinal))
                {
                    if (!sourceResolved)
                    {
                        return Unknown(
                            "The source GameObject cannot be resolved, so its unconnected state cannot be proven.");
                    }
                    return PrefabCreateEvidence.IsOutermostInstanceOf(source, path)
                        ? Unknown(
                            "The source is connected to the pre-existing Prefab the step never rewrote.")
                        : (ReconcileResult)new ReconcileResult.NotApplied();
                }
                if (!overwrite)
                {
                    return Unknown(
                        "A pre-existing artifact occupies the target path; it cannot be attributed to the step.");
                }
                // overwrite: the changed content must equal the recorded
                // expectation — checked below with the shared fingerprint
                // comparison (§G6.1 comparability conditions included).
            }

            // The artifact's content must equal the expected fingerprint
            // (§G6.1 items 6–7) — a pre-existing artifact, a foreign or
            // modified file and an uncomputable fingerprint all fail closed.
            var check = PrefabCreateEvidence.CheckSavedAsset(expected, path);
            if (check is not PrefabFingerprintCheck.Matched)
            {
                return Unknown(check is PrefabFingerprintCheck.Unknown unknown
                    ? "The saved Prefab content cannot be compared: " + unknown.Reason
                    : "The artifact's content differs from the fingerprint the step recorded.");
            }

            var outputs = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            try
            {
                outputs[PrefabPort] =
                    new RefResolver().ToRef(asset!, "unity.PrefabAssetRef");
            }
            catch (ArgumentException)
            {
                return Unknown("The Prefab asset could not be published as a reference.");
            }

            if (connect)
            {
                // The row's connect clause: same session, the source is the
                // outermost instance of the saved Prefab.
                var recordedSession = (string)sessionToken!;
                if (!string.Equals(
                        recordedSession, EditorSessionId.GetOrCreate(),
                        StringComparison.Ordinal))
                {
                    return Unknown(
                        "The snapshot belongs to a different Editor session, so the connected source cannot be re-identified.");
                }
                if (!sourceResolved ||
                    !PrefabCreateEvidence.IsOutermostInstanceOf(source, path))
                {
                    return Unknown(
                        "The source is not verifiably the outermost instance of the saved Prefab.");
                }
                try
                {
                    outputs[InstancePort] =
                        new RefResolver().ToRef(source!, "unity.PrefabInstanceRef");
                }
                catch (ArgumentException)
                {
                    return Unknown(
                        "The connected source could not be published as a reference.");
                }
            }
            return new ReconcileResult.Completed(outputs);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The §I.2 effects column's "(overwrite 時 +asset.modify)": a
        /// constant <c>overwrite: false</c> (or the absent default) keeps
        /// <c>{asset.create, scene.modify}</c>; a constant true or a value
        /// not statically known reports the superset including
        /// <c>asset.modify</c>.
        /// </remarks>
        public IReadOnlyList<string> ResolveEffects(StaticContext context)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }
            return MayOverwrite(context.Parameters)
                ? new[]
                {
                    EffectNames.AssetCreate, EffectNames.SceneModify,
                    EffectNames.AssetModify,
                }
                : new[] { EffectNames.AssetCreate, EffectNames.SceneModify };
        }

        // True unless the constant overwrite value is provably false: an
        // absent port takes the false default; a present non-boolean or
        // true may rewrite.
        internal static bool MayOverwrite(
            IReadOnlyDictionary<string, InputValue> parameters)
        {
            if (!parameters.TryGetValue(OverwritePort, out var input) ||
                !input.IsPresent)
            {
                return false;
            }
            var token = input.Value!.ToToken();
            return token.Type != JTokenType.Boolean ||
                (bool)((JValue)token).Value!;
        }

        // The current content fingerprint of the artifact at the path, or
        // null when it cannot be computed — the compare target of the
        // recorded pre-state fingerprint for the overwrite branch.
        private static string? CurrentFingerprint(string path)
        {
            var result = PrefabContentFingerprint.ComputeAsset(path);
            return result is PrefabFingerprintResult.Computed computed
                ? computed.Fingerprint.Hash
                : null;
        }

        private static bool TryResolveSource(JObject sourceRef, out GameObject? source)
        {
            var resolved = new RefResolver().Resolve<GameObject>(
                JsonValue.From(sourceRef));
            source = resolved.Ok ? resolved.Value : null;
            return resolved.Ok && source is not null;
        }

        private sealed class StepInputs
        {
            internal string Path = string.Empty;
            internal bool Connect = true;
            internal bool Overwrite;
        }

        // The validated inputs of a Preflight/Execute call: the source
        // resolves through §G2 (its own OW-UNITY-001/002/003/004
        // diagnostics pass through), the path through the shared write
        // policy, the flags through their present-or-default reads.
        private static Result<StepInputs> ReadInputs(
            StepContext ctx, out GameObject? source)
        {
            source = null;
            try
            {
                source = ctx.Get<GameObject>(SourcePort);
            }
            catch (StepContextException failure)
            {
                return Result<StepInputs>.Failure(failure.Diagnostics);
            }
            if (source == null)
            {
                return Result<StepInputs>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_001,
                    "The referenced source GameObject no longer exists.",
                    port: SourcePort));
            }
            var path = PrefabCreateEvidence.CheckPrefabWritePath(
                ctx.Get<string>(PathPort), PathPort);
            if (!path.Ok)
            {
                return Result<StepInputs>.Failure(path.Diagnostics);
            }
            return Result<StepInputs>.Success(new StepInputs
            {
                Path = path.Value!,
                Connect = !ctx.IsPresent(ConnectPort) ||
                    ctx.Get<bool>(ConnectPort),
                Overwrite = ctx.IsPresent(OverwritePort) &&
                    ctx.Get<bool>(OverwritePort),
            });
        }

        private static bool Member(
            JObject obj, string name, JTokenType type, out JToken? value)
        {
            value = obj[name];
            return value is not null && value.Type == type;
        }

        private static ReconcileResult Unknown(string reason) =>
            new ReconcileResult.Unknown(reason);

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Save a scene GameObject as a Prefab asset, optionally connecting the source to it.")
            .Ja(
                "GameObject から Prefab を作成",
                "シーンの GameObject を Prefab アセットとして保存する。connect 時は元オブジェクトがその outermost インスタンスになる。",
                "Prefab", "作成", "GameObject")
            .Input(SourcePort, "unity.GameObjectRef", required: true)
            .Input(PathPort, "unity.AssetPath", required: true)
            .Input(ConnectPort, "core.Bool", required: false,
                @default: s_defaultConnect)
            .Input(OverwritePort, "core.Bool", required: false,
                @default: s_defaultOverwrite)
            .Output(PrefabPort, "unity.PrefabAssetRef")
            .Output(InstancePort, "unity.PrefabInstanceRef")
            .Effects(EffectNames.AssetCreate, EffectNames.SceneModify)
            .Undo(UndoSupport.Partial)
            .Idempotency(Idempotency.Reconcilable)
            .Requires("path_writable", PathPort)
            .Build() with
            {
                DisplayName = "Create Prefab from GameObject",
                Description =
                    "Saves the referenced scene GameObject as a Prefab asset at the " +
                    "given .prefab path. With 'connect' (the default) the source " +
                    "becomes the outermost instance of the new Prefab and the step " +
                    "also publishes the instance; with connect:false the source is " +
                    "left a plain GameObject. An existing path fails with " +
                    "OW-UNITY-005 unless 'overwrite' is true, which adds the " +
                    "asset.modify side effect and may only rewrite an existing " +
                    "Prefab asset. The Operation is Reconcilable: after an " +
                    "interruption it completes only when the artifact's content " +
                    "fingerprint equals the fingerprint recorded before the save " +
                    "and, for connect, the source is verifiably connected in the " +
                    "same Editor session (SPEC §F5, §G6.1).",
                Category = "Prefab",
                Tags = new[] { "prefab", "create", "gameobject" },
                Keywords = new[] { "prefab", "create", "save", "gameobject", "connect" },
                Examples = new[]
                {
                    new OperationExample(
                        "Turn a found GameObject into a connected Prefab",
                        PrefabCreateOperationExamples.CreateFromGameObject),
                },
            };
    }
}
