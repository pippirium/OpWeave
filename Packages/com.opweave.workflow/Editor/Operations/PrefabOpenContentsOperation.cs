#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;

namespace OpWeave.Operations
{
    /// <summary>
    /// <c>unity.prefab.open_contents</c> (SPEC §I.2, §G6 item 5): opens the
    /// contents of a Prefab asset with
    /// <see cref="PrefabUtility.LoadPrefabContents(string)"/> and registers them
    /// as the Run resource <c>prefab_edit</c> (SPEC §E6 item 4), which
    /// <c>unity.prefab.save_contents</c> saves and releases and which the Runner
    /// releases at Run end when the Run never saved them (§G6 item 5, §E6 item 4).
    /// Read-only, <see cref="ExecutionMode.Immediate"/>, no Undo,
    /// <see cref="Idempotency.Reconcilable"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The acquisition is journaled before the contents open</b>, so every
    /// contents this step can open are recorded before they exist: a stop
    /// between the two leaves an open acquisition for recovery instead of
    /// contents nobody owns. A re-execution never duplicates them (SPEC §F5
    /// common rule 4): while this step's <em>own</em> acquisition of the pair
    /// is open and its contents are live, <see cref="Execute"/> hands out that
    /// editing root instead of loading a second copy, and the pair is acquired
    /// once rather than once per attempt — the §E6 item 4 fold pairs one
    /// <c>resource_released</c> with one <c>resource_acquired</c>. A second
    /// open_contents <em>step</em> of the same Prefab is not such a
    /// re-execution, and it is refused rather than treated as one: a Run holds
    /// at most one contents session of a Prefab (see below). The mirror case,
    /// releasing contents this session no longer holds, is a no-op success (see
    /// <see cref="PrefabContentsResources.Release"/>).
    /// </para>
    /// <para>
    /// <b>One session of a Prefab per Run.</b> SPEC §G2 represents every
    /// GameObject inside opened contents with the scene key
    /// <c>prefab-contents:&lt;guid&gt;</c> and resolves it from the Run
    /// resource, so two live sessions of one guid give that one key two
    /// candidates: each of them answers the other's steps too, and §J's verdict
    /// for an ambiguous reference (参照が曖昧) is OW-UNITY-002. §F5's
    /// <c>open_contents</c> row grants Completed only to the same step's own
    /// resource precisely because a second open of the same Prefab cannot be
    /// attributed to it, and a session no step of this Run can point at is a
    /// session its <c>save_contents</c> can never save and its Run-end release
    /// can never release. So both <see cref="Preflight"/> and
    /// <see cref="Execute"/> refuse the second open of a Prefab this Run
    /// already holds — before acquiring and before loading, so the refusal
    /// journals nothing and opens nothing — and the ownership question
    /// downstream steps face stays the simple one: the Run's session of that
    /// Prefab is the one it owns.
    /// </para>
    /// <para>
    /// <b>No instance state.</b> The opened contents are re-found by enumerating
    /// the preview scenes of the journaled guid (SPEC §C2 item 4; the L-06 probe
    /// verified on 6000.0.85f1 that the preview scene and its unsaved
    /// modifications survive a domain reload while the managed reference does
    /// not), so no <see cref="GameObject"/> is ever cached on the instance. The
    /// enumeration takes the user's own Prefab Mode stage of the same asset out
    /// of its candidates: its stage scene carries the asset path too, but it is
    /// not this Run's resource and no verdict of this step may unload it.
    /// </para>
    /// <para>
    /// <b>Which root this step owns.</b> That enumeration answers "contents of
    /// this Prefab are open", never "these are the ones this step opened" — and
    /// because the acquisition is journaled before the load, an interrupted or
    /// failed load leaves an open acquisition with no contents of its own while
    /// somebody else's session of the same asset is open. So after each
    /// successful load the step binds the root it loaded to its own acquisition
    /// scope (<see cref="PrefabContentsOwnership"/>, Editor-session state that
    /// outlives a domain reload exactly as the preview scene does and dies with
    /// an Editor restart), and this step adopts or completes on that bound root
    /// alone. Nothing here unloads anything: the §E6 item 4 release handler
    /// unloads exactly this acquisition's own bound root, and with no binding to
    /// point at it leaves every open session alone.
    /// </para>
    /// </remarks>
    [OpWeaveOperation("unity.prefab.open_contents", 1)]
    public sealed class PrefabOpenContentsOperation : OperationBase
    {
        /// <inheritdoc/>
        public override OperationDescriptor Descriptor { get; } = BuildDescriptor();

        /// <inheritdoc/>
        public override PreflightResult Preflight(StepContext ctx)
        {
            // SPEC §D8 item 1: an edge-fed prefab has no value until its
            // producer runs, so the Dry Run records no target evidence for
            // the step — the same empty result a missing port yields.
            if (ctx.IsPending("prefab"))
            {
                return new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(),
                    PrefabContentsSnapshots.Empty);
            }
            var target = ResolveTarget(ctx);
            if (!target.Ok)
            {
                return new PreflightResult(
                    target.Diagnostics, Array.Empty<PlannedChange>(),
                    PrefabContentsSnapshots.Empty);
            }
            var asset = target.Value!;
            // The one-session-per-Run rule of the class remarks, answered before
            // this step records its snapshot: acquiring here would journal an
            // acquisition of a Prefab whose contents the Run cannot identify, and
            // loading would add the second session that makes every
            // prefab-contents Ref of the target ambiguous (§G2, §J OW-UNITY-002).
            // This step's own still-open acquisition is not that conflict — a
            // re-execution adopts it below.
            var heldByTheRun = ContentsOfPrefabAlreadyHeldByTheRun(
                (IRunResourceRegistry)ctx, asset);
            if (heldByTheRun is not null)
            {
                return new PreflightResult(
                    new[] { heldByTheRun }, Array.Empty<PlannedChange>(),
                    PrefabContentsSnapshots.Empty);
            }
            // The artifact's own pre-execution state: how many contents of this
            // Prefab were already open when the step began. A later
            // reconciliation that finds contents open without an acquisition of
            // its own reads this to tell "this step opened them" from
            // "something else had them open" (SPEC §F5 common rule 4).
            var openAtPreflight =
                PrefabContentsResources.FindEditingRoots(asset.Path).Count;
            return new PreflightResult(
                Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(),
                PrefabContentsSnapshots.Open(
                    asset.Guid, asset.Path, openAtPreflight));
        }

        /// <inheritdoc/>
        public override StepResult Execute(StepContext ctx)
        {
            var target = ResolveTarget(ctx);
            if (!target.Ok)
            {
                return StepResult.Failed(target.Diagnostics[0]);
            }
            var asset = target.Value!;
            var registry = (IRunResourceRegistry)ctx;
            var identity = PrefabContentsResources.Data(asset.Guid, asset.Path);

            // A re-execution adopts this step's own still-open acquisition
            // rather than opening a second copy (SPEC §F5: the row's Completed
            // evidence is the same step's resource, §G6 item 5). A sibling
            // step's acquisition of the same Prefab is not this step's and is
            // never adopted here — and it is not acquired over either: a Run
            // holds one contents session of a Prefab (see the class remarks).
            // Two contents sessions of one Prefab cannot be told apart, so the
            // ambiguous state fails instead of guessing which root is this
            // step's (OW-UNITY-002); an acquisition whose contents were never
            // loaded still gets its load here, without a second record.
            var openState = registry.IsOpen(PrefabContentsResources.Kind, identity);
            if (openState == RunResourceOpenState.Undetermined)
            {
                // An open acquisition whose acquiring step is not recorded:
                // neither this step's to adopt nor closed, so loading a copy
                // here could duplicate the artifact §F5 forbids.
                return StepResult.Failed(ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_002,
                    "The open Prefab contents of the target cannot be attributed " +
                    "to a step, so this step cannot open its own without " +
                    "duplicating them.",
                    port: "prefab"));
            }
            if (openState == RunResourceOpenState.OpenByThisStep)
            {
                var open = PrefabContentsResources.FindEditingRoots(asset.Path);
                if (open.Count > 1)
                {
                    // Two sessions of one Prefab: the §G2 prefab-contents lookup
                    // cannot tell them apart, so the root this step would publish
                    // is one no step downstream can resolve.
                    return StepResult.Failed(ErrorCodes.Create(
                        ErrorCodes.OW_UNITY_002,
                        "Two contents sessions of the target Prefab are already " +
                        "open, so this step's own contents cannot be identified.",
                        port: "prefab"));
                }
                // A re-execution adopts the root this step's own acquisition
                // bound when it loaded the contents — never a root chosen because
                // it matches the asset path, which would hand this step another
                // owner's editing session and publish it as its own output (SPEC
                // §F5: the evidence is the same step's resource, §G6 item 5).
                var owned = PrefabContentsOwnership.FindOwned(
                    registry.AcquisitionScope, asset.Guid, asset.Path);
                if (owned is not null)
                {
                    ctx.SetOutput("root", owned);
                    return StepResult.Succeeded();
                }
                if (open.Count == 1)
                {
                    // The one session of the target that is open is not the one
                    // this step's acquisition ever bound: somebody else's work.
                    // Adopting it would claim it, and loading a copy would add a
                    // second session of the Prefab nobody can tell apart.
                    return StepResult.Failed(ErrorCodes.Create(
                        ErrorCodes.OW_UNITY_002,
                        "The open contents of the target Prefab cannot be " +
                        "attributed to this step, so this step can neither " +
                        "adopt them nor open its own without duplicating them.",
                        port: "prefab"));
                }
                // Nothing of the target is open at all: the acquisition this step
                // journaled was never followed by a load, so the load below
                // completes it — without a second record.
            }
            else
            {
                // A second session of a Prefab the Run already holds would give
                // the §G2 prefab-contents:<guid> key two candidates, so neither
                // this step's output nor the other session's could be resolved
                // by any downstream step. Refuse before the acquisition and
                // before the load — nothing is journaled, nothing is opened (see
                // the class remarks).
                var heldByTheRun = ContentsOfPrefabAlreadyHeldByTheRun(registry, asset);
                if (heldByTheRun is not null)
                {
                    return StepResult.Failed(heldByTheRun);
                }
                // The resource record precedes the load — see the class remarks.
                registry.Acquire(PrefabContentsResources.Kind, identity);
            }

            GameObject contents;
            try
            {
                contents = PrefabUtility.LoadPrefabContents(asset.Path);
            }
            catch (Exception e)
            {
                // The asset resolved, so this is no longer §G2's missing
                // reference: Unity could not produce the asset's contents.
                return StepResult.Failed(LoadFailed(e.GetType().Name));
            }
            if (contents == null)
            {
                return StepResult.Failed(LoadFailed(null));
            }

            // The contents exist now, so the open acquisition finally has an
            // object of its own: bind it to this step's acquisition scope, which
            // is what lets this Run's release — now, after a domain reload, or
            // after a later re-execution — tell its own contents from another
            // session of the same Prefab. Bound only past the load, so an
            // acquisition that never got here owns no root and releases nothing.
            PrefabContentsOwnership.Bind(
                registry.AcquisitionScope, asset.Guid, contents);

            ctx.SetOutput("root", contents);
            return StepResult.Succeeded();
        }

        /// <inheritdoc/>
        public override ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot)
        {
            // The §F5 evidence row of SPEC §I.2: the acquisition this step
            // recorded, and a live editing root of that same Prefab.
            var recorded = PrefabContentsSnapshots.ReadOpen(snapshot);
            if (recorded is null)
            {
                return new ReconcileResult.Unknown(
                    "The step's Prefab contents snapshot is unreadable.");
            }
            RunResourceOpenState openState;
            try
            {
                openState = ((IRunResourceRegistry)ctx).IsOpen(
                    PrefabContentsResources.Kind, recorded.ResourceData);
            }
            catch (Exception e) when (e is NotSupportedException || e is ArgumentException)
            {
                // The Unknown row of the §I.2 table: "the state of the Run
                // resource cannot be determined".
                return new ReconcileResult.Unknown(
                    "The state of the step's Run resource cannot be determined.");
            }

            var roots = PrefabContentsResources.FindEditingRoots(recorded.Path);
            if (openState == RunResourceOpenState.Undetermined)
            {
                // An open acquisition of the target whose acquiring step the
                // journal never recorded: the §F5 row's "state of the Run
                // resource cannot be determined" — neither its Completed nor
                // its NotApplied condition holds.
                return new ReconcileResult.Unknown(
                    "An open acquisition of the target records no acquiring step, " +
                    "so the state of the step's Run resource cannot be determined.");
            }
            if (openState == RunResourceOpenState.OpenByThisStep)
            {
                // Completed needs every clause of the row: this step's resource
                // alive, and the editing root of the target its own acquisition
                // bound when it loaded the contents. Contents that were already
                // open when the step began are somebody else's artifact however
                // many remain (§F5 common rule 4); none means this step's are
                // gone — an Editor restart discards the preview scene along with
                // the ownership binding — and one root that matches the asset
                // path without that binding is another owner's session, which the
                // step may neither adopt nor publish as its output. Two roots stay
                // Unknown even when one of them is bound: the §G2
                // prefab-contents lookup cannot disambiguate them, so the restored
                // output would name a scene key no step downstream can resolve
                // (§F5 rule 6), and NeedsReview decides rather than handing on a
                // reference that resolves to nothing.
                if (recorded.OpenAtPreflight != 0)
                {
                    return new ReconcileResult.Unknown(
                        "Contents of the target Prefab were already open when the " +
                        "step began, so the open cannot be attributed to this step.");
                }
                if (roots.Count == 0)
                {
                    return new ReconcileResult.Unknown(
                        "The acquired Prefab contents are no longer open in this Editor session.");
                }
                if (roots.Count > 1)
                {
                    return new ReconcileResult.Unknown(
                        "Two contents sessions of the target Prefab are open, so this step's contents cannot be identified.");
                }
                var owned = PrefabContentsOwnership.FindOwned(
                    ((IRunResourceRegistry)ctx).AcquisitionScope,
                    PrefabContentsResources.GuidOf(recorded.ResourceData) ?? "",
                    recorded.Path);
                return owned is not null
                    ? CompletedWith(owned)
                    : new ReconcileResult.Unknown(
                        "The open Prefab contents of the target cannot be attributed to this step.");
            }
            if (roots.Count != 0)
            {
                // Contents of the target are open that this step never recorded
                // acquiring: a partial side effect, a sibling step's session, or
                // a pre-existing same-name artifact. Re-executing would open a
                // second copy and adopting them would claim another owner's work.
                return new ReconcileResult.Unknown(
                    "Open Prefab contents of the target exist that this step did not acquire.");
            }
            // Nothing open now and nothing open when the step began: the effect
            // provably did not happen, and re-running opens exactly one session.
            return recorded.OpenAtPreflight == 0
                ? new ReconcileResult.NotApplied()
                : new ReconcileResult.Unknown(
                    "Contents of the target Prefab were already open when the step began, so the open cannot be attributed to this step.");
        }

        // The one-session-per-Run rule both Preflight and Execute apply, stated
        // once so the two can never disagree: the Run holds an acquisition of
        // this Prefab that this step may not adopt. IsHeldByRun answers "the Run
        // holds one" (§E6 item 4's pairing scope), IsOpen answers "this step
        // holds its own" (§F5's evidence), and only the second is a state this
        // step completes on — a re-execution, and nothing else, adopts it.
        private static Diagnostic? ContentsOfPrefabAlreadyHeldByTheRun(
            IRunResourceRegistry registry, PrefabAsset asset)
        {
            var identity = PrefabContentsResources.Data(asset.Guid, asset.Path);
            return registry.IsHeldByRun(PrefabContentsResources.Kind, identity) &&
                registry.IsOpen(
                    PrefabContentsResources.Kind, identity)
                    != RunResourceOpenState.OpenByThisStep
                ? ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_002,
                    "The Run already holds an open contents session of the " +
                    "target Prefab, so this step cannot open a second one whose " +
                    "references would be ambiguous.",
                    port: "prefab")
                : null;
        }

        // The LoadPrefabContents failure of an asset that resolved: §J has no
        // §G6 code for it, and OW-UNITY-005 (an invalid path or a collision with
        // an existing asset) cannot describe the asset's own path, nor does
        // OW-UNITY-001 (the reference target is missing) describe an asset §G2
        // just resolved. OW-UNITY-008 — the asset's content could not be
        // produced or imported — is the §J meaning that fits, and its retryable
        // verdict suits a read Unity may well succeed at on the next attempt.
        private static Diagnostic LoadFailed(string? exceptionName) =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_008,
                exceptionName is null
                    ? "The Prefab contents could not be opened."
                    : "The Prefab contents could not be opened: " + exceptionName,
                port: "prefab");

        // The Completed verdict: the live editing root becomes the step's
        // declared output Ref, restorable for the steps downstream (SPEC §F5
        // rule 6).
        private static ReconcileResult CompletedWith(GameObject root)
        {
            try
            {
                return new ReconcileResult.Completed(
                    new Dictionary<string, JsonValue>
                    {
                        ["root"] = new RefResolver().ToRef(root, "unity.GameObjectRef"),
                    });
            }
            catch (ArgumentException)
            {
                return new ReconcileResult.Unknown(
                    "The opened Prefab contents could not be published as a reference.");
            }
        }

        // The referenced Prefab asset behind the "prefab" port (§G2 resolves the
        // Ref and reports OW-UNITY-001/002/004 itself; this adds the §G6 need:
        // the imported asset's own guid and path).
        private static Result<PrefabAsset> ResolveTarget(StepContext ctx)
        {
            var asset = ctx.Get<GameObject>("prefab");
            var path = asset is null ? "" : AssetDatabase.GetAssetPath(asset);
            var guid = string.IsNullOrEmpty(path) ? "" : AssetDatabase.AssetPathToGUID(path);
            return string.IsNullOrEmpty(guid)
                ? Result<PrefabAsset>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_001,
                    "The referenced Prefab asset is not an imported project asset.",
                    port: "prefab"))
                : Result<PrefabAsset>.Success(new PrefabAsset(guid, path!));
        }

        private static OperationDescriptor BuildDescriptor() => Op
            .Define("unity.prefab.open_contents", 1)
            .Summary("Open a Prefab's contents for editing and hold them as a Run resource until save_contents or the Run end.")
            .Ja(
                "Prefab の編集内容を開く",
                "Prefab アセットの内容を編集用に開き、Run リソースとして保持する。save_contents か Run 終了まで解放されない。",
                "Prefab", "編集", "内容")
            .Input("prefab", "unity.PrefabAssetRef", required: true)
            .Output("root", "unity.GameObjectRef")
            .Effects(EffectNames.Read)
            .Idempotency(Idempotency.Reconcilable)
            .Build() with
            {
                DisplayName = "Open Prefab contents",
                Description =
                    "Loads the contents of the referenced Prefab asset through Unity's " +
                    "Prefab content editing and hands out the editing root. The opened " +
                    "contents are a Run resource: they stay open after this step, are " +
                    "saved and released by unity.prefab.save_contents, and are discarded " +
                    "and released at Run end when the Run never saved them. No asset file " +
                    "changes before save_contents, and a compile, code.write, " +
                    "code.template or package.modify node between the two is rejected with " +
                    "OW-VAL-012. A Run holds one contents session of a Prefab: opening a " +
                    "second one of the same Prefab fails with OW-UNITY-002, because both " +
                    "sessions answer the same prefab-contents reference.",
                Category = "Prefab",
                Tags = new[] { "prefab", "contents", "edit" },
                Keywords = new[] { "prefab", "contents", "open", "edit", "load" },
                Examples = new[] { PrefabContentsExamples.OpenContents },
            };

        // The imported asset the "prefab" port names.
        private sealed record PrefabAsset(string Guid, string Path);
    }
}
