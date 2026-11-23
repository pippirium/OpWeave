#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace OpWeave.Operations
{
    /// <summary>
    /// <c>unity.prefab.save_contents</c> (SPEC §I.2, §G6 item 5): saves the
    /// contents <c>unity.prefab.open_contents</c> opened back into their source
    /// Prefab asset with <see cref="PrefabUtility.SaveAsPrefabAsset(GameObject, string)"/>,
    /// unloads them with <see cref="PrefabUtility.UnloadPrefabContents(GameObject)"/>
    /// and releases the <c>prefab_edit</c> Run resource (SPEC §E6 item 4).
    /// <c>asset.modify</c>, <see cref="ExecutionMode.Immediate"/>, no Undo,
    /// <see cref="Idempotency.Reconcilable"/> — and per the §I.2 row its
    /// <see cref="Reconcile"/> never answers
    /// <see cref="ReconcileResult.NotApplied"/>: Completed only when the saved
    /// asset's content fingerprint equals the fingerprint Preflight recorded
    /// (SPEC §G6.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The destination is the contents' own source.</b> The opened contents
    /// live in a preview scene whose path is the asset they came from, so saving
    /// needs no destination port — and cannot point at a different asset than the
    /// one open_contents opened (§G6 item 5 names the Prefab <c>root</c> came
    /// from).
    /// </para>
    /// <para>
    /// <b>Only the Run's own session may be saved.</b> A root handed to the
    /// <c>root</c> port names an open session of a Prefab through the §G2
    /// <c>prefab-contents:&lt;guid&gt;</c> key, which cannot distinguish the one
    /// this Run opened from one somebody else did. Both <see cref="Preflight"/>
    /// and <see cref="Execute"/> therefore require the ownership proof
    /// <see cref="PrefabContentsOwnership"/> holds — one of this Run's
    /// acquisitions bound this very root when its load produced it — and refuse
    /// anything else with OW-UNITY-002 without writing, unloading or releasing
    /// (§E6 item 4: a release reaches only the Run's own resource). With
    /// <c>open_contents</c> holding one session of a Prefab per Run, the Run's
    /// bound session is the only candidate, so the save's unload, unbind and
    /// release all pair with that one acquisition.
    /// </para>
    /// <para>
    /// <b>The reconciliation evidence.</b> Preflight records the expected
    /// fingerprint, <c>prefabFingerprintVersion</c>, the target guid/path and the
    /// Unity version (§G6.1 item 4), plus the destination's own pre-execution
    /// fingerprint — §F5 common rule 4 forbids counting a pre-existing same-name
    /// artifact as this step's product. A content state the fingerprint cannot
    /// normalize (a missing Script, an unresolvable reference) saves normally but
    /// records no expected fingerprint, and reconciliation then reports Unknown,
    /// which the Runner turns into NeedsReview: never a cheaper existence,
    /// name or timestamp comparison (§G6.1 禁止).
    /// </para>
    /// </remarks>
    [OpWeaveOperation("unity.prefab.save_contents", 1)]
    public sealed class PrefabSaveContentsOperation : OperationBase
    {
        /// <inheritdoc/>
        public override OperationDescriptor Descriptor { get; } = BuildDescriptor();

        /// <inheritdoc/>
        public override PreflightResult Preflight(StepContext ctx)
        {
            var session = OpenedContents(ctx);
            if (!session.Ok)
            {
                return new PreflightResult(
                    session.Diagnostics, Array.Empty<PlannedChange>(),
                    PrefabContentsSnapshots.Empty);
            }
            var contents = session.Value!;

            // §G6.1 item 1: read-only walk of the content about to be saved. The
            // snapshot the adapter forms from it carries §G6.1 item 4's expected
            // fingerprint, version, target and Unity version — or, when the
            // content cannot be normalized (a missing Script, an unresolvable
            // reference), the same target and versions with the Unknown reason
            // recorded rather than hidden: the save stays valid, its recovery
            // cannot be proven by fingerprint. It also records the destination's
            // own pre-execution fingerprint — §F5 common rule 4 forbids counting
            // a pre-existing same-name artifact as this step's product.
            var expected = PrefabContentFingerprint.ComputeForSave(
                contents.Root, contents.Path);
            return new PreflightResult(
                Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(),
                PrefabContentsSnapshots.Save(
                    expected, contents.Guid, contents.Path));
        }

        /// <inheritdoc/>
        public override StepResult Execute(StepContext ctx)
        {
            var session = OpenedContents(ctx);
            if (!session.Ok)
            {
                return StepResult.Failed(session.Diagnostics[0]);
            }
            var contents = session.Value!;

            GameObject saved;
            try
            {
                saved = PrefabUtility.SaveAsPrefabAsset(contents.Root, contents.Path);
            }
            catch (Exception e)
            {
                return StepResult.Failed(SaveFailed(
                    "the write failed with " + e.GetType().Name, contents.Path));
            }
            if (saved == null)
            {
                return StepResult.Failed(
                    SaveFailed("the asset could not be written", contents.Path));
            }

            // The editing session closes here; a Run that never reached this
            // point releases the contents through the §E6 item 4 Run-end release.
            // The id is read before the unload because the unload destroys the
            // object, and the open step's ownership binding of the session that
            // no longer exists goes with it.
            var rootId = PrefabContentsOwnership.SessionIdOf(contents.Root);
            PrefabUtility.UnloadPrefabContents(contents.Root);
            PrefabContentsOwnership.UnbindObject(rootId);

            // The acquisition this step was handed belongs to its open_contents
            // step, so the §E6 item 4 pairing rule — not this step's own §F5
            // evidence — decides whether there is an open pair to close.
            var registry = (IRunResourceRegistry)ctx;
            var identity = PrefabContentsResources.Data(contents.Guid, contents.Path);
            if (registry.IsHeldByRun(PrefabContentsResources.Kind, identity))
            {
                registry.Release(PrefabContentsResources.Kind, identity);
            }

            ctx.Changes.Saved(
                new ChangeTarget("asset", contents.Path, contents.Guid), undoable: false);

            // The Ref names the persisted asset, re-read after the import the save
            // performed — never the contents object the unload just destroyed.
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(contents.Path);
            if (asset == null)
            {
                return StepResult.Failed(ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_008,
                    "The saved Prefab asset could not be loaded after the save.",
                    port: "root"));
            }
            ctx.SetOutput("prefab", asset);
            return StepResult.Succeeded();
        }

        /// <inheritdoc/>
        public override ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot)
        {
            // The §I.2 row leaves NotApplied out for this Operation: a
            // destination holding the pre-save content is indistinguishable from
            // one this step saved and another operation later changed.
            if (PrefabContentsSnapshots.IsUnreadable(snapshot))
            {
                return new ReconcileResult.Unknown(
                    "The step's Prefab contents snapshot is unreadable.");
            }
            var path = PrefabContentsSnapshots.SavedTargetPath(snapshot);
            if (string.IsNullOrEmpty(path))
            {
                return new ReconcileResult.Unknown(
                    "The step's snapshot records no save destination.");
            }

            // Read-only walk of the destination: the same contents form the
            // expected fingerprint was computed from (§G6.1 items 4 and 6).
            GameObject contents;
            try
            {
                contents = PrefabUtility.LoadPrefabContents(path!);
            }
            catch (Exception e)
            {
                return new ReconcileResult.Unknown(
                    "The destination Prefab contents could not be read: " + e.GetType().Name);
            }
            PrefabFingerprintCheck check;
            try
            {
                check = PrefabContentFingerprint.Check(snapshot, contents);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
            if (check is not PrefabFingerprintCheck.Matched)
            {
                return new ReconcileResult.Unknown(check is
                    PrefabFingerprintCheck.Unknown unknown
                    ? "The saved Prefab content cannot be compared: " + unknown.Reason
                    : "The saved Prefab content differs from the fingerprint the step recorded.");
            }

            // §F5 common rule 4: a destination that already held this exact
            // content before the step ran is a pre-existing artifact, so this
            // step's own write stays unproven.
            if (PrefabContentsSnapshots.SavedDestinationAlreadyHeldContent(snapshot))
            {
                return new ReconcileResult.Unknown(
                    "The destination already held the saved content before the step ran.");
            }
            return CompletedWith(path!);
        }

        // The Completed verdict: the saved asset's PrefabAssetRef, restorable
        // from the recorded target path (SPEC §F5 rule 6).
        private static ReconcileResult CompletedWith(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null)
            {
                return new ReconcileResult.Unknown(
                    "The saved Prefab asset could not be loaded to restore the step's output.");
            }
            try
            {
                return new ReconcileResult.Completed(
                    new Dictionary<string, JsonValue>
                    {
                        ["prefab"] = new RefResolver().ToRef(asset, "unity.PrefabAssetRef"),
                    });
            }
            catch (ArgumentException)
            {
                return new ReconcileResult.Unknown(
                    "The saved Prefab asset could not be published as a reference.");
            }
        }

        // The "root" port resolved to an open contents session (§G2 resolves the
        // Ref and reports OW-UNITY-001/002/003/004 itself; this adds the §G6
        // requirement that the object really is an opened Prefab editing root).
        private static Result<Contents> OpenedContents(StepContext ctx)
        {
            var root = ctx.Get<GameObject>("root");
            var notContents = ErrorCodes.Create(
                ErrorCodes.OW_UNITY_004,
                "The root is not the editing root of opened Prefab contents.",
                port: "root");
            if (root == null)
            {
                return Result<Contents>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_001,
                    "The referenced Prefab contents root no longer exists.",
                    port: "root"));
            }
            var scene = root.scene;
            var path = scene.IsValid() ? scene.path : "";
            if (!scene.IsValid() || string.IsNullOrEmpty(path) ||
                !EditorSceneManager.IsPreviewScene(scene) ||
                !path!.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                return Result<Contents>.Failure(notContents);
            }
            // A Prefab Mode stage of the same asset sits in a scene that carries
            // that asset's path too. Saving it would write the user's session
            // over the asset and unloading it would close the session, so the
            // stage is refused here exactly as the Run-resource enumeration
            // refuses it (see PrefabContentsResources.FindEditingRoots).
            // GetPrefabStage takes the GameObject in 6000.0 and answers for the
            // stage that object belongs to, so no open stage can slip through.
            if (PrefabStageUtility.GetPrefabStage(root) != null)
            {
                return Result<Contents>.Failure(notContents);
            }
            var guid = AssetDatabase.AssetPathToGUID(path);
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (string.IsNullOrEmpty(guid) || asset == null ||
                PrefabUtility.GetPrefabAssetType(asset) == PrefabAssetType.NotAPrefab)
            {
                return Result<Contents>.Failure(notContents);
            }
            // The §G2 prefab-contents:<guid> key names *a* session of this
            // Prefab, never the one this Run opened, and this step is never the
            // step that opened it. So the write requires the proof
            // <see cref="PrefabContentsOwnership"/> holds: one of this Run's
            // acquisitions bound this very root when its load produced it. A
            // root no scope of this Run ever bound is somebody else's editing
            // session — another tool's LoadPrefabContents, the user's own code,
            // a session opened after an Editor restart while this Run's cleanup
            // was still outstanding — and saving it would publish their unsaved
            // work as this step's product, then close their session with the
            // unload. SPEC §E6 item 4 gives a Run its own resource only, so the
            // foreign root is refused with §J's ambiguous reference and nothing
            // changes: no save, no unload, no release, no change record.
            if (!PrefabContentsOwnership.IsBoundInRun(
                    ((IRunResourceRegistry)ctx).AcquisitionScope,
                    PrefabContentsOwnership.SessionIdOf(root)))
            {
                return Result<Contents>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_002,
                    "The contents root is not the session this Run opened, so " +
                    "this step cannot save it as its own.",
                    port: "root"));
            }
            return Result<Contents>.Success(
                new Contents(root, guid, path));
        }

        // The write Unity could not perform: OW-UNITY-005 means an invalid path
        // or a collision with an existing asset, and neither describes a write
        // to the asset the contents were opened from — the path resolved and the
        // asset exists. OW-UNITY-008 (the asset could not be produced or
        // imported) is the §J code whose meaning fits a SaveAsPrefabAsset that
        // returned nothing or threw, and its retryable verdict suits a write the
        // Editor may well complete on the next attempt.
        private static Diagnostic SaveFailed(string reason, string path) =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_008,
                "The opened Prefab contents could not be saved to '" + path + "': " + reason,
                port: "root");

        private static OperationDescriptor BuildDescriptor() => Op
            .Define("unity.prefab.save_contents", 1)
            .Summary("Save the opened Prefab contents back to their Prefab asset and release the editing session.")
            .Ja(
                "Prefab の編集内容を保存",
                "開いている Prefab の編集内容を元の Prefab アセットに保存し、編集セッションを解放する。",
                "Prefab", "保存")
            .Input("root", "unity.GameObjectRef", required: true)
            .Output("prefab", "unity.PrefabAssetRef")
            .Effects(EffectNames.AssetModify)
            .Idempotency(Idempotency.Reconcilable)
            .Build() with
            {
                DisplayName = "Save Prefab contents",
                Description =
                    "Writes the contents opened by unity.prefab.open_contents back into the " +
                    "Prefab asset they came from, unloads the editing session and releases the " +
                    "Run resource. Preflight walks the contents read-only and records their " +
                    "content fingerprint, so a recovery compares the saved asset with that " +
                    "fingerprint only — an existing file, a name or a timestamp never completes " +
                    "the step. Contents whose fingerprint cannot be computed save normally but " +
                    "end recovery in NeedsReview.",
                Category = "Prefab",
                Tags = new[] { "prefab", "contents", "save" },
                Keywords = new[] { "prefab", "contents", "save", "write", "asset" },
                Examples = new[] { PrefabContentsExamples.SaveContents },
            };

        // The open contents session a root belongs to: the source asset's guid
        // and path it saves back to.
        private sealed class Contents
        {
            internal Contents(GameObject root, string guid, string path)
            {
                Root = root;
                Guid = guid;
                Path = path;
            }

            internal GameObject Root { get; }
            internal string Guid { get; }
            internal string Path { get; }
        }
    }
}
