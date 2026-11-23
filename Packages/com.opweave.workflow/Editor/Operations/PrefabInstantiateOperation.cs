#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpWeave.Operations
{
    /// <summary>
    /// <c>unity.prefab.instantiate</c> (SPEC §I.2, §G6 item 2): instantiates
    /// the referenced Prefab asset into a scene with
    /// <see cref="PrefabUtility.InstantiatePrefab(UnityEngine.Object, Scene)"/>,
    /// optionally under a parent at a given local position, and publishes
    /// the outermost instance root as a <c>unity.PrefabInstanceRef</c>.
    /// With no <c>parent</c> the instance lands at the scene's root; with
    /// no <c>scene</c> either, in the parent's or the active scene (the
    /// §I.1 precedence). Immediate, Undo <see cref="UndoSupport.Full"/>,
    /// <see cref="Idempotency.Reconcilable"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The §F5 evidence row: Preflight records the source Prefab's guid,
    /// path and content fingerprint (SPEC §G6.1), the target scene and
    /// parent (the root list when none), the parent's ordered children
    /// (handle · globalId · name), the position input and the session id.
    /// <see cref="Reconcile"/> works only inside the recorded session:
    /// an unchanged child list is <see cref="ReconcileResult.NotApplied"/>;
    /// exactly one new child that is an outermost instance of the recorded
    /// Prefab — still fingerprint-identical — with no override beyond the
    /// placement and a matching position is
    /// <see cref="ReconcileResult.Completed"/> with the instance output
    /// restored; anything else (a different session, zero or several new
    /// children, a non-instance child, a changed source Prefab, an extra
    /// override, unrestorable evidence) is
    /// <see cref="ReconcileResult.Unknown"/>.
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class PrefabInstantiateOperation : OperationBase
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.prefab.instantiate";

        internal const string PrefabPort = "prefab";
        internal const string ScenePort = "scene";
        internal const string ParentPort = "parent";
        internal const string PositionPort = "position";
        internal const string InstancePort = "instance";

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
            if (ctx.IsPending(PrefabPort) || ctx.IsPending(ScenePort) ||
                ctx.IsPending(ParentPort) || ctx.IsPending(PositionPort) ||
                !ctx.IsPresent(PrefabPort))
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

            var snapshot = new JObject
            {
                ["prefab"] = new JObject
                {
                    ["guid"] = plan.PrefabGuid,
                    ["path"] = plan.PrefabPath,
                },
                // The source Prefab's own content fingerprint: a changed
                // base at recovery makes the artifact unattributable —
                // the evidence is recorded read-only (§G6.1 item 1).
                ["base"] = PrefabCreateEvidence.FingerprintEvidence(
                    PrefabContentFingerprint.ComputeAsset(plan.PrefabPath),
                    plan.PrefabGuid, plan.PrefabPath).ToToken(),
                ["scene"] = GameObjectEvidence.SceneKey(plan.Scene),
                ["parent"] = plan.Parent is null
                    ? JValue.CreateNull()
                    : GameObjectEvidence.RefOf(plan.Parent),
                ["children"] = plan.Parent is null
                    ? GameObjectEvidence.ChildrenOf(plan.Scene)
                    : GameObjectEvidence.ChildrenOf(plan.Parent.transform),
                ["editorSessionId"] = EditorSessionId.GetOrCreate(),
            };
            if (plan.PositionSpecified)
            {
                snapshot["position"] = new JObject
                {
                    ["x"] = plan.Position.x,
                    ["y"] = plan.Position.y,
                    ["z"] = plan.Position.z,
                };
            }
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
            if (plan.PositionSpecified &&
                !GameObjectEvidence.IsFinite(plan.Position))
            {
                return StepResult.Failed(ErrorCodes.Create(
                    ErrorCodes.OW_TYPE_003,
                    "The position is not a finite Vector3.",
                    port: PositionPort));
            }

            var instance =
                PrefabUtility.InstantiatePrefab(plan.PrefabAsset, plan.Scene)
                as GameObject;
            if (instance == null)
            {
                return StepResult.Failed(PrefabCreateEvidence.WriteFailed(
                    PrefabPort, "the Prefab instance could not be created"));
            }
            Undo.RegisterCreatedObjectUndo(
                instance, "Instantiate Prefab " + plan.PrefabPath);
            if (plan.Parent is not null)
            {
                Undo.SetTransformParent(
                    instance.transform, plan.Parent.transform, false,
                    "Instantiate Prefab");
            }
            if (plan.PositionSpecified)
            {
                instance.transform.localPosition = plan.Position;
            }

            ctx.Changes.Created(
                new ChangeTarget(
                    "gameObject",
                    RefHierarchyPath.For(instance),
                    null,
                    GlobalObjectId.GetGlobalObjectIdSlow(instance).ToString()),
                undoable: true);
            ctx.SetOutput(InstancePort, instance);
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
            if (obj["prefab"] is not JObject prefab ||
                prefab["guid"] is not JValue { Type: JTokenType.String } guidToken ||
                prefab["path"] is not JValue { Type: JTokenType.String } pathToken ||
                obj["base"] is not { } baseEvidence ||
                obj["scene"] is not JObject sceneKey ||
                obj["parent"] is not { } parentToken ||
                obj["children"] is not JArray children ||
                !Member(obj, "editorSessionId", JTokenType.String, out var sessionToken))
            {
                return Unknown(
                    "The Preflight snapshot is missing a required evidence member.");
            }
            // §F5 common rule 3: both verdicts of this row are the
            // same-session ones — the child list is a session-handle set.
            if (!string.Equals(
                (string)sessionToken!, EditorSessionId.GetOrCreate(),
                StringComparison.Ordinal))
            {
                return Unknown("The snapshot belongs to a different Editor session.");
            }
            var prefabGuid = (string)guidToken.Value!;
            var prefabPath = (string)pathToken.Value!;

            Vector3? expectedPosition = null;
            if (obj["position"] is { } positionToken)
            {
                if (positionToken is not JObject positionObj ||
                    !ReadComponent(positionObj, "x", out var x) ||
                    !ReadComponent(positionObj, "y", out var y) ||
                    !ReadComponent(positionObj, "z", out var z))
                {
                    return Unknown(
                        "The snapshot's position is not a readable Vector3.");
                }
                expectedPosition = new Vector3(x, y, z);
            }

            var scene = GameObjectEvidence.ResolveScene(sceneKey);
            if (scene is null || !scene.Value.isLoaded)
            {
                return Unknown(
                    "The snapshot's scene cannot be resolved to a loaded scene.");
            }
            var parent = GameObjectEvidence.ResolveParent(parentToken, out var isRoot);
            if (!isRoot && parent is null)
            {
                return Unknown("The snapshot's parent GameObject cannot be resolved.");
            }
            if (isRoot && parentToken.Type != JTokenType.Null)
            {
                return Unknown("The snapshot's parent member is malformed.");
            }
            var live = GameObjectEvidence.LiveChildren(parent?.transform, scene.Value);

            // NotApplied: the parent's child list is exactly the snapshot's —
            // the step provably added nothing and re-execution only creates.
            if (GameObjectEvidence.SameChildList(children, live))
            {
                return new ReconcileResult.NotApplied();
            }

            // Completed needs every snapshot child still present and exactly
            // one new child — the artifact.
            var snapshotHandles = GameObjectEvidence.SnapshotHandles(children);
            GameObject? added = null;
            var addedCount = 0;
            var missingSnapshotChild = false;
            foreach (var entry in children)
            {
                if (GameObjectEvidence.HandleOf(entry) is { } oldRaw)
                {
                    var stillThere = false;
                    foreach (var child in live)
                    {
                        if (SessionHandle.RawOf(child) == oldRaw)
                        {
                            stillThere = true;
                            break;
                        }
                    }
                    if (!stillThere)
                    {
                        missingSnapshotChild = true;
                        break;
                    }
                }
            }
            foreach (var child in live)
            {
                if (!snapshotHandles.Contains(SessionHandle.RawOf(child)))
                {
                    added = child;
                    addedCount++;
                }
            }
            if (missingSnapshotChild || addedCount != 1 || added is null)
            {
                return Unknown(
                    "The parent's children differ from the snapshot by something " +
                    "other than exactly one new child.");
            }

            // The artifact must be an outermost instance of the recorded
            // Prefab — a same-named plain GameObject or an instance of
            // another asset is never this step's product.
            if (!PrefabCreateEvidence.IsOutermostInstanceOf(added, prefabPath))
            {
                return Unknown(
                    "The new child is not an outermost instance of the recorded Prefab.");
            }
            var instanceGuid = AssetDatabase.AssetPathToGUID(
                PrefabCreateEvidence.NormalizedInstancePath(added));
            if (!string.Equals(instanceGuid, prefabGuid, StringComparison.Ordinal))
            {
                return Unknown(
                    "The new child's Prefab source guid differs from the recorded one.");
            }

            // The source Prefab itself must be unchanged — a modified base
            // makes the instance's content unattributable.
            var baseCheck = PrefabCreateEvidence.CheckSavedAsset(
                baseEvidence, prefabPath);
            if (baseCheck is not PrefabFingerprintCheck.Matched)
            {
                return Unknown(baseCheck is PrefabFingerprintCheck.Unknown unknown
                    ? "The source Prefab content cannot be compared: " + unknown.Reason
                    : "The source Prefab changed since the step's snapshot was taken.");
            }

            // No override beyond the placement set, and the position the
            // input asked for — or the Prefab's own root position.
            var overrideReason =
                PrefabCreateEvidence.UnexpectedInstanceOverride(added);
            if (overrideReason is not null)
            {
                return Unknown(
                    "The new child carries an override beyond the placement: " +
                    overrideReason + ".");
            }
            var sourceRoot =
                AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (sourceRoot == null)
            {
                return Unknown("The source Prefab asset could not be loaded.");
            }
            if (expectedPosition is { } wanted)
            {
                if (!GameObjectEvidence.SameVector(
                        wanted, added.transform.localPosition))
                {
                    return Unknown(
                        "The new child's position does not match the step's input.");
                }
            }
            else if (!GameObjectEvidence.SameVector(
                sourceRoot.transform.localPosition, added.transform.localPosition))
            {
                return Unknown(
                    "The new child's position differs from the Prefab's own.");
            }
            if (Quaternion.Angle(
                    sourceRoot.transform.localRotation,
                    added.transform.localRotation) > 0.001f)
            {
                return Unknown(
                    "The new child's rotation differs from the Prefab's own.");
            }

            try
            {
                return new ReconcileResult.Completed(
                    new Dictionary<string, JsonValue>(StringComparer.Ordinal)
                    {
                        [InstancePort] =
                            new RefResolver().ToRef(added, "unity.PrefabInstanceRef"),
                    });
            }
            catch (ArgumentException)
            {
                return Unknown(
                    "The instance could not be published as a reference.");
            }
        }

        private sealed class Placement
        {
            internal GameObject PrefabAsset = null!;
            internal string PrefabGuid = string.Empty;
            internal string PrefabPath = string.Empty;
            internal Scene Scene;
            internal GameObject? Parent;
            internal bool PositionSpecified;
            internal Vector3 Position;
        }

        // The validated inputs: the Prefab asset resolves through §G2 (its
        // own OW-UNITY-001/004 diagnostics pass through); the placement
        // mirrors unity.gameobject.create — an explicit scene wins, a
        // parent alone names its own scene, and a parent in a different
        // scene than the explicit one is the §G2 "keys disagree" failure.
        private static Result<Placement> ReadInputs(StepContext ctx)
        {
            GameObject? prefabAsset;
            Scene scene;
            GameObject? parent;
            try
            {
                prefabAsset = ctx.Get<GameObject>(PrefabPort);
                parent = ctx.IsPresent(ParentPort)
                    ? ctx.Get<GameObject>(ParentPort)
                    : null;
                var hasScene = ctx.IsPresent(ScenePort);
                scene = hasScene
                    ? ctx.Get<Scene>(ScenePort)
                    : parent is not null
                        ? parent.scene
                        : SceneManager.GetActiveScene();
                if (hasScene && parent is not null &&
                    parent.scene.handle != scene.handle)
                {
                    return Result<Placement>.Failure(ErrorCodes.Create(
                        ErrorCodes.OW_UNITY_002,
                        "The parent GameObject is not in the target scene.",
                        port: ParentPort));
                }
            }
            catch (StepContextException failure)
            {
                return Result<Placement>.Failure(failure.Diagnostics);
            }
            if (prefabAsset == null)
            {
                return Result<Placement>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_001,
                    "The referenced Prefab asset no longer exists.",
                    port: PrefabPort));
            }
            var prefabPath = AssetDatabase.GetAssetPath(prefabAsset);
            var prefabGuid = string.IsNullOrEmpty(prefabPath)
                ? string.Empty
                : AssetDatabase.AssetPathToGUID(prefabPath);
            if (string.IsNullOrEmpty(prefabGuid) ||
                PrefabUtility.GetPrefabAssetType(prefabAsset) ==
                    PrefabAssetType.NotAPrefab)
            {
                return Result<Placement>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_004,
                    "The referenced asset is not a Prefab.",
                    port: PrefabPort));
            }
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return Result<Placement>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_003,
                    "The target scene is not loaded.",
                    port: ScenePort));
            }
            var placement = new Placement
            {
                PrefabAsset = prefabAsset,
                PrefabGuid = prefabGuid,
                PrefabPath = prefabPath!,
                Scene = scene,
                Parent = parent,
            };
            if (ctx.IsPresent(PositionPort))
            {
                placement.PositionSpecified = true;
                placement.Position = ctx.Get<Vector3>(PositionPort);
            }
            return Result<Placement>.Success(placement);
        }

        private static bool ReadComponent(
            JObject obj, string name, out float value)
        {
            value = 0f;
            if (obj[name] is not JValue
                { Type: JTokenType.Float or JTokenType.Integer } token)
            {
                return false;
            }
            var component = token.ToObject<float>();
            if (float.IsNaN(component) || float.IsInfinity(component))
            {
                return false;
            }
            value = component;
            return true;
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
            .Summary("Instantiate a Prefab asset into a scene, optionally under a parent at a position.")
            .Ja(
                "Prefab をシーンに配置",
                "Prefab アセットのインスタンスをシーンに生成する。parent 省略時はルート、scene 省略時はアクティブシーン。",
                "Prefab", "配置", "インスタンス")
            .Input(PrefabPort, "unity.PrefabAssetRef", required: true)
            .Input(ScenePort, "unity.SceneRef", required: false)
            .Input(ParentPort, "unity.GameObjectRef", required: false)
            .Input(PositionPort, "unity.Vector3", required: false)
            .Output(InstancePort, "unity.PrefabInstanceRef")
            .Effects(EffectNames.SceneModify)
            .Undo(UndoSupport.Full)
            .Idempotency(Idempotency.Reconcilable)
            .Requires("scene_loaded", ScenePort)
            .Build() with
            {
                DisplayName = "Instantiate Prefab",
                Description =
                    "Instantiates the referenced Prefab asset into the given scene " +
                    "through Unity's Prefab instantiation and publishes the " +
                    "outermost instance root as a PrefabInstanceRef. With no parent " +
                    "the instance lands at the scene's root; with no scene either, " +
                    "in the parent's or the active scene. The optional position is " +
                    "the instance root's local position. The Operation is " +
                    "Reconcilable for the same Editor session: after an " +
                    "interruption it completes only when exactly one new child " +
                    "under the recorded parent is that Prefab's outermost " +
                    "instance, the source Prefab's content fingerprint is " +
                    "unchanged and no override beyond the placement is recorded " +
                    "(SPEC §F5).",
                Category = "Prefab",
                Tags = new[] { "prefab", "instance", "scene" },
                Keywords = new[] { "prefab", "instantiate", "spawn", "instance", "place" },
                Examples = new[]
                {
                    new OperationExample(
                        "Instantiate a Prefab at a position",
                        PrefabCreateOperationExamples.Instantiate,
                        PrefabCreateOperationExamples.FixtureManifest),
                },
            };
    }
}
