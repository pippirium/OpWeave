#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace OpWeave.Operations
{
    /// <summary>
    /// <c>unity.prefab.create_variant</c> (SPEC §I.2, §G6 item 4): creates a
    /// Prefab Variant of the <c>base</c> Prefab at <c>path</c> by
    /// instantiating the base inside a temporary Preview Scene
    /// (<see cref="EditorSceneManager.NewPreviewScene"/>) and saving that
    /// instance with
    /// <see cref="PrefabUtility.SaveAsPrefabAsset(GameObject, string)"/> —
    /// the user's scenes are never touched. An occupied path refuses with
    /// OW-UNITY-005. Immediate, Undo <see cref="UndoSupport.None"/>,
    /// <see cref="Idempotency.Reconcilable"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The §F5 evidence row: Preflight records the normalized path and its
    /// nothing-there state, the base Prefab's guid, path and content
    /// fingerprint, and the expected content fingerprint of a fresh
    /// Variant — computed by trial-instantiating the base into a
    /// discarded Preview Scene and reading it read-only through
    /// <see cref="PrefabContentFingerprint.ComputeForSave"/> (§G6.1 item 1;
    /// no asset write, no user scene). <see cref="Reconcile"/> returns
    /// NotApplied only while the path is still empty; Completed only when
    /// the path holds a new Prefab Variant whose base is the recorded guid,
    /// the base's own content fingerprint still matches the snapshot and
    /// the Variant's content fingerprint equals the recorded expectation —
    /// a different base, a recorded override or an uncomputable fingerprint
    /// is Unknown (NeedsReview), never a cheaper comparison.
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class PrefabCreateVariantOperation : OperationBase
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.prefab.create_variant";

        internal const string BasePort = "base";
        internal const string PathPort = "path";
        internal const string VariantPort = "variant";

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
            // §D8 item 1: an edge-fed port has no value to inspect in a Dry
            // Run, and a missing required port yields no evidence to record.
            if (ctx.IsPending(BasePort) || ctx.IsPending(PathPort) ||
                !ctx.IsPresent(BasePort) || !ctx.IsPresent(PathPort))
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
            var destination = PrefabCreateEvidence.CheckDestination(
                plan.Path, PathPort, overwrite: false);
            if (destination is not null)
            {
                return new PreflightResult(
                    new[] { destination }, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }

            var before = PrefabCreateEvidence.PathBeforeState(plan.Path);
            var snapshot = new JObject
            {
                ["path"] = plan.Path,
                ["pathExisted"] = !((bool?)before["absent"] ?? false),
                ["basePrefab"] = new JObject
                {
                    ["guid"] = plan.BaseGuid,
                    ["path"] = plan.BasePath,
                },
                ["base"] = PrefabCreateEvidence.FingerprintEvidence(
                    PrefabContentFingerprint.ComputeAsset(plan.BasePath),
                    plan.BaseGuid, plan.BasePath).ToToken(),
                ["expected"] = ExpectedVariantFingerprint(
                    plan.BaseAsset, plan.Path).ToToken(),
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
            var inputs = ReadInputs(ctx);
            if (!inputs.Ok)
            {
                return StepResult.Failed(inputs.Diagnostics[0]);
            }
            var plan = inputs.Value!;
            var destination = PrefabCreateEvidence.CheckDestination(
                plan.Path, PathPort, overwrite: false);
            if (destination is not null)
            {
                return StepResult.Failed(destination);
            }

            // §G6 item 4: the Variant is built in a temporary Preview
            // Scene; the user's scenes are never opened or marked.
            var preview = EditorSceneManager.NewPreviewScene();
            try
            {
                var instance = PrefabUtility.InstantiatePrefab(
                    plan.BaseAsset, preview) as GameObject;
                if (instance == null)
                {
                    return StepResult.Failed(PrefabCreateEvidence.WriteFailed(
                        BasePort, "the base Prefab could not be instantiated"));
                }
                GameObject? saved;
                try
                {
                    saved = PrefabUtility.SaveAsPrefabAsset(instance, plan.Path);
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
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
            }

            ctx.Changes.Created(
                new ChangeTarget(
                    "asset", plan.Path,
                    AssetDatabase.AssetPathToGUID(plan.Path)),
                undoable: false);
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(plan.Path);
            if (asset == null)
            {
                return StepResult.Failed(PrefabCreateEvidence.WriteFailed(
                    PathPort, "the saved asset could not be loaded after the save"));
            }
            ctx.SetOutput(VariantPort, asset);
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
                obj["basePrefab"] is not JObject basePrefab ||
                basePrefab["guid"] is not JValue { Type: JTokenType.String } guidToken ||
                basePrefab["path"] is not JValue { Type: JTokenType.String } basePathToken ||
                obj["base"] is not { } baseEvidence ||
                obj["expected"] is not { } expected ||
                !Member(obj, "editorSessionId", JTokenType.String, out _))
            {
                return Unknown(
                    "The Preflight snapshot is missing a required evidence member.");
            }
            var path = (string)pathToken!;
            var pathExisted = (bool)existedToken!;
            var baseGuid = (string)guidToken.Value!;
            var basePath = (string)basePathToken.Value!;

            if (!PrefabCreateEvidence.OccupiedNow(path))
            {
                // Nothing at the path: provably not applied — but only when
                // the snapshot recorded an empty pre-state; a vanished
                // pre-existing artifact is an inconsistent state.
                return pathExisted
                    ? Unknown(
                        "The snapshot recorded an existing artifact at the path, which is now gone.")
                    : (ReconcileResult)new ReconcileResult.NotApplied();
            }
            if (pathExisted)
            {
                return Unknown(
                    "A pre-existing artifact occupies the target path; it cannot be attributed to the step.");
            }

            // The artifact must be an imported Prefab Variant with a new
            // guid whose base is the recorded base — a plain Prefab, a
            // foreign asset or a Variant of another source is never this
            // step's product.
            var physical = AssetArtifact.ToPhysicalPath(path);
            var guid = AssetDatabase.AssetPathToGUID(
                path, AssetPathToGUIDOptions.OnlyExistingAssets);
            var main = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (physical is null || !System.IO.File.Exists(physical) ||
                guid.Length == 0 || main == null ||
                PrefabUtility.GetPrefabAssetType(main) != PrefabAssetType.Variant)
            {
                return Unknown(
                    "The artifact at the target path is not an imported Prefab Variant.");
            }
            if (string.Equals(guid, baseGuid, StringComparison.Ordinal))
            {
                return Unknown(
                    "The artifact carries the base Prefab's own guid.");
            }

            // The recorded base must still be intact — a changed base makes
            // the Variant's expected content unattributable.
            var recordedBasePath = AssetDatabase.GUIDToAssetPath(baseGuid);
            if (string.IsNullOrEmpty(recordedBasePath) ||
                !string.Equals(
                    recordedBasePath, basePath, StringComparison.Ordinal))
            {
                return Unknown("The recorded base Prefab no longer resolves at its recorded path.");
            }
            var baseCheck = PrefabCreateEvidence.CheckSavedAsset(
                baseEvidence, recordedBasePath);
            if (baseCheck is not PrefabFingerprintCheck.Matched)
            {
                return Unknown(baseCheck is PrefabFingerprintCheck.Unknown unknown
                    ? "The base Prefab content cannot be compared: " + unknown.Reason
                    : "The base Prefab changed since the step's snapshot was taken.");
            }

            // The Variant's own base identity and complete content: the
            // contents root of a Variant is itself an instance of the base,
            // and its fingerprint must equal the fresh-Variant expectation.
            GameObject contents;
            try
            {
                contents = PrefabUtility.LoadPrefabContents(path);
            }
            catch (Exception e)
            {
                return Unknown(
                    "The Variant contents could not be read: " + e.GetType().Name);
            }
            if (contents == null)
            {
                return Unknown("The Variant contents could not be read.");
            }
            PrefabFingerprintCheck check;
            try
            {
                var variantBaseGuid = AssetDatabase.AssetPathToGUID(
                    PrefabCreateEvidence.NormalizedInstancePath(contents));
                if (!string.Equals(variantBaseGuid, baseGuid, StringComparison.Ordinal))
                {
                    return Unknown(
                        "The Variant's base is not the recorded base Prefab.");
                }
                check = PrefabCreateEvidence.CheckFingerprint(expected, contents);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
            if (check is not PrefabFingerprintCheck.Matched)
            {
                return Unknown(check is PrefabFingerprintCheck.Unknown unknown
                    ? "The Variant content cannot be compared: " + unknown.Reason
                    : "The Variant content differs from the fingerprint the step recorded.");
            }

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            try
            {
                return new ReconcileResult.Completed(
                    new Dictionary<string, JsonValue>(StringComparer.Ordinal)
                    {
                        [VariantPort] = new RefResolver().ToRef(
                            asset!, "unity.PrefabAssetRef"),
                    });
            }
            catch (ArgumentException)
            {
                return Unknown(
                    "The Variant asset could not be published as a reference.");
            }
        }

        // The §F5 row's "Base から作った直後に期待される内容": a fresh
        // instance of the base in a discarded Preview Scene, fingerprinted
        // the way the save will persist it (root name normalized to the
        // destination file stem). Read-only toward every asset; an
        // uncomputable expectation is recorded with its reason and fails
        // closed at recovery.
        private static JsonValue ExpectedVariantFingerprint(
            GameObject baseAsset, string destinationPath)
        {
            var preview = EditorSceneManager.NewPreviewScene();
            try
            {
                var instance = PrefabUtility.InstantiatePrefab(
                    baseAsset, preview) as GameObject;
                if (instance == null)
                {
                    return PrefabCreateEvidence.FingerprintEvidence(
                        new PrefabFingerprintResult.Unknown(
                            "the base Prefab could not be instantiated for the expected fingerprint"),
                        null, destinationPath);
                }
                return PrefabCreateEvidence.FingerprintEvidence(
                    PrefabContentFingerprint.ComputeForSave(
                        instance, destinationPath),
                    null, destinationPath);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
            }
        }

        private sealed class StepInputs
        {
            internal GameObject BaseAsset = null!;
            internal string BaseGuid = string.Empty;
            internal string BasePath = string.Empty;
            internal string Path = string.Empty;
        }

        // The validated inputs: the base resolves through §G2 as a
        // PrefabAssetRef (its own OW-UNITY-001/004 diagnostics pass
        // through) and must be an imported Prefab asset; the path through
        // the shared .prefab write policy.
        private static Result<StepInputs> ReadInputs(StepContext ctx)
        {
            GameObject? baseAsset;
            try
            {
                baseAsset = ctx.Get<GameObject>(BasePort);
            }
            catch (StepContextException failure)
            {
                return Result<StepInputs>.Failure(failure.Diagnostics);
            }
            if (baseAsset == null)
            {
                return Result<StepInputs>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_001,
                    "The referenced base Prefab asset no longer exists.",
                    port: BasePort));
            }
            var basePath = AssetDatabase.GetAssetPath(baseAsset);
            var baseGuid = string.IsNullOrEmpty(basePath)
                ? string.Empty
                : AssetDatabase.AssetPathToGUID(basePath);
            if (string.IsNullOrEmpty(baseGuid) ||
                PrefabUtility.GetPrefabAssetType(baseAsset) ==
                    PrefabAssetType.NotAPrefab)
            {
                return Result<StepInputs>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_004,
                    "The referenced asset is not a Prefab.",
                    port: BasePort));
            }
            var path = PrefabCreateEvidence.CheckPrefabWritePath(
                ctx.Get<string>(PathPort), PathPort);
            if (!path.Ok)
            {
                return Result<StepInputs>.Failure(path.Diagnostics);
            }
            return Result<StepInputs>.Success(new StepInputs
            {
                BaseAsset = baseAsset,
                BaseGuid = baseGuid,
                BasePath = basePath!,
                Path = path.Value!,
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
            .Summary("Create a Prefab Variant of a base Prefab at a new .prefab path.")
            .Ja(
                "Prefab Variant を作成",
                "既存の Prefab を基底とする Prefab Variant を指定パスに作成する。一時的な Preview Scene で作り、ユーザーの Scene には触れない。",
                "Prefab", "Variant", "作成")
            .Input(BasePort, "unity.PrefabAssetRef", required: true)
            .Input(PathPort, "unity.AssetPath", required: true)
            .Output(VariantPort, "unity.PrefabAssetRef")
            .Effects(EffectNames.AssetCreate)
            .Undo(UndoSupport.None)
            .Idempotency(Idempotency.Reconcilable)
            .Requires("path_writable", PathPort)
            .Build() with
            {
                DisplayName = "Create Prefab Variant",
                Description =
                    "Creates a Prefab Variant of the referenced base Prefab at the " +
                    "given .prefab path. The Variant is assembled by instantiating " +
                    "the base inside a temporary Preview Scene and saving that " +
                    "instance — the user's scenes are never touched. An existing " +
                    "path fails with OW-UNITY-005. The Operation is Reconcilable: " +
                    "after an interruption it completes only when the artifact is " +
                    "a new Variant of the recorded base guid whose content " +
                    "fingerprint equals the recorded fresh-Variant expectation " +
                    "(SPEC §F5, §G6.1).",
                Category = "Prefab",
                Tags = new[] { "prefab", "variant", "create" },
                Keywords = new[] { "prefab", "variant", "create", "derive" },
                Examples = new[]
                {
                    new OperationExample(
                        "Create a Variant of the HUD Prefab",
                        PrefabCreateOperationExamples.CreateVariant,
                        PrefabCreateOperationExamples.FixtureManifest),
                },
            };
    }
}
