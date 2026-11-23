#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Persistence;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The SPEC §G6 prefab-contents Run resource (SPEC §E6 item 4): the resource
    /// kind and <c>(kind, data)</c> identity <c>unity.prefab.open_contents</c>
    /// acquires and <c>unity.prefab.save_contents</c> releases, the re-find that
    /// turns a journaled guid back into the live editing root, and the §E6
    /// item 4 release handler the runner host registers for the kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the preview scene is the identity.</b> The contents
    /// <see cref="PrefabUtility.LoadPrefabContents(string)"/> opens live in an
    /// isolated preview scene whose <c>path</c> is the source Prefab's asset
    /// path, and — verified on 6000.0.85f1 and the later generations by the
    /// L-06 probe — that scene and its unsaved modifications survive a domain
    /// reload while the managed reference the opening step held does not. The
    /// prefab-instance APIs cannot re-identify the contents root
    /// (<c>GetPrefabAssetPathOfNearestInstanceRoot</c> answers <c>""</c> and
    /// <c>IsPartOfPrefabAsset</c> is false there), so the enumeration below is
    /// the re-find path L-06 verified: root-level objects of a preview scene
    /// named after the asset path. No cache is kept on the Operation — SPEC §C2
    /// item 4 forbids instance state, and a managed reference would not survive
    /// a reload anyway. The one piece of state that does exist, the ownership
    /// binding of <see cref="PrefabContentsOwnership"/>, is Editor-session
    /// state: like the preview scene it outlives a domain reload, and like the
    /// contents themselves it is gone after an Editor restart — exactly like the
    /// release evidence of the same class, which is why an absent binding with
    /// absent evidence is never read as a completed release.
    /// </para>
    /// <para>
    /// <b>One resource per acquisition, and never a guess.</b> Opening the same
    /// Prefab twice opens two contents in two preview scenes (L-06 case b3),
    /// and nothing on the contents object says which acquisition owns which
    /// scene. A Run never produces that pair itself any more —
    /// <c>unity.prefab.open_contents</c> refuses a second session of a Prefab
    /// its Run already holds — so a second session of one asset found beside
    /// the Run's own was opened by somebody else and stays theirs. What tells
    /// them apart is the Editor-session ownership binding
    /// (<see cref="PrefabContentsOwnership"/>): the step that loaded the
    /// contents records the root it loaded against its own acquisition scope —
    /// its Run id and the step of its <c>resource_acquired</c> record — and a
    /// release unloads only a root that binding proves belongs to the
    /// acquisition it is releasing. A (kind, data) pair names the Prefab, never
    /// who opened it, so an unbound session is another owner's editing session
    /// and survives: the acquisition it was handed stays journaled open — the
    /// fail-safe side of SPEC §E6 item 4's two-fact model, which review resolves
    /// rather than a guess deleting somebody's unsaved edits. Because the
    /// acquisition is journaled <em>before</em> the load, an interrupted or
    /// failed load leaves exactly such a binding-less acquisition, which is why
    /// guessing on "the one root that matches the path" was never safe.
    /// </para>
    /// </remarks>
    internal static class PrefabContentsResources
    {
        /// <summary>
        /// The SPEC §E6 item 4 resource kind of an open prefab-contents
        /// editing session; the journal records and the release dispatcher
        /// pair on it.
        /// </summary>
        internal const string Kind = "prefab_edit";

        /// <summary>
        /// The <c>(kind, data)</c> identity of the contents opened from the
        /// Prefab asset <paramref name="prefabGuid"/> at
        /// <paramref name="assetPath"/> — the guid is the release and
        /// reconciliation identity, the path is the recorded context the
        /// §F5 snapshot's own evidence names (SPEC §G6.1: the target's guid and
        /// path).
        /// </summary>
        internal static JsonValue Data(string prefabGuid, string assetPath) =>
            JsonValue.From(new JObject
            {
                ["guid"] = prefabGuid,
                ["path"] = assetPath,
            });

        /// <summary>
        /// The Prefab guid a contents resource identity carries, or
        /// <see langword="null"/> when it carries none.
        /// </summary>
        internal static string? GuidOf(JsonValue? data)
        {
            if (data?.ToToken() is not JObject obj)
            {
                return null;
            }
            var guid = obj["guid"];
            return guid is JValue { Type: JTokenType.String } value
                ? (string?)value.Value
                : null;
        }

        /// <summary>
        /// The live editing roots the contents of the Prefab asset at
        /// <paramref name="assetPath"/> opened into — one per open
        /// <c>LoadPrefabContents</c> of that asset, in enumeration order.
        /// Returns nothing when no contents of that asset are open (never
        /// opened, already unloaded, or discarded by an Editor restart).
        /// <b>A Prefab Mode stage of the same asset is never one of them:</b>
        /// its stage scene carries the asset path too, but it is the user's
        /// own editing session, not a Run resource, and unloading it would
        /// destroy work the Run never opened.
        /// </summary>
        internal static IReadOnlyList<GameObject> FindEditingRoots(string assetPath)
        {
            var found = new List<GameObject>();
            if (string.IsNullOrEmpty(assetPath))
            {
                return found;
            }
            // FindObjectsOfTypeAll reaches the objects of preview scenes, which
            // the scene APIs do not list (L-06's re-find path); the persistent
            // asset copies among them fail the IsPreviewScene test below.
            foreach (var candidate in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (candidate == null || candidate.transform.parent != null)
                {
                    continue;
                }
                var scene = candidate.scene;
                if (!scene.IsValid() || !EditorSceneManager.IsPreviewScene(scene))
                {
                    continue;
                }
                // Prefab Mode keeps its stage scene under the asset path as
                // well, so the path test alone cannot tell the Run's contents
                // from the user's session. 6000.0 exposes GetPrefabStage on a
                // GameObject only — there is no Scene overload — and it names
                // the stage that object's scene belongs to (null for the plain
                // preview scene LoadPrefabContents opens), so asking about the
                // root itself covers every open stage rather than only the
                // active one GetCurrentPrefabStage() would name.
                if (PrefabStageUtility.GetPrefabStage(candidate) != null)
                {
                    continue;
                }
                if (string.Equals(scene.path, assetPath, StringComparison.Ordinal))
                {
                    found.Add(candidate);
                }
            }
            return found;
        }

        /// <summary>
        /// The single live editing root of the Prefab
        /// <paramref name="prefabGuid"/> opened into — the SPEC §G2
        /// <c>prefab-contents:&lt;guid&gt;</c> lookup the runner hands the
        /// <see cref="RefResolver"/>. An ambiguous state (no open contents, or
        /// two contents of the same Prefab) answers
        /// <see langword="null"/>: the reference then resolves to nothing
        /// (OW-UNITY-003) rather than to one of two candidates guessed by
        /// enumeration order.
        /// </summary>
        internal static GameObject? FindEditingRoot(string prefabGuid)
        {
            var path = AssetDatabase.GUIDToAssetPath(prefabGuid);
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            var roots = FindEditingRoots(path);
            return roots.Count == 1 ? roots[0] : null;
        }

        /// <summary>
        /// The SPEC §E6 item 4 release handler of <see cref="Kind"/>: unloads
        /// the editing root <em>this acquisition</em> opened, so contents a Run
        /// never saved are discarded at Run end — including after a failed or
        /// cancelled step (SPEC §G6 item 5). Which root that is comes from the
        /// Editor-session binding <paramref name="runId"/> and the resource's
        /// own <c>AcquiringStep</c> name (the acquisition scope), never from
        /// matching an asset path: the acquisition is journaled before the
        /// contents exist, so an interrupted or failed load leaves one with no
        /// root of its own, and the only sessions of the Prefab that are open
        /// then belong to somebody else — an unrelated
        /// <c>LoadPrefabContents</c>, another step's leftover, the user's own
        /// code, or a session that appeared after an Editor restart while
        /// recovery cleanup was still outstanding. Unloading one of those would
        /// destroy work this Run never opened, so the release fails safe and
        /// the acquisition stays journaled open for review. Releasing what this
        /// Editor session no longer holds succeeds without unloading anything:
        /// after a restart there is no contents object left to release, and the
        /// journaled acquisition must still close.
        /// <para>
        /// <b>The second proof, for an interrupted release.</b> A release whose
        /// unload succeeded but whose <c>resource_released</c> record never
        /// reached the store — the window the Run-end cleanup leaves between one
        /// physical release and its own record — leaves this handler with no
        /// binding of its own while another acquisition of the same Prefab is
        /// still open. The release-evidence table of
        /// <see cref="PrefabContentsOwnership"/> tells that state from one whose
        /// contents were never loaded at all: it names the object this very
        /// scope unloaded, and it survives a domain reload exactly like the
        /// bindings, because both are Editor-session state that an Editor
        /// restart discards together with the contents themselves. Proved, this
        /// acquisition owns nothing any more and succeeds without unloading —
        /// which is what lets its durable pairing close instead of holding the
        /// next identical acquisition open forever. Not proved, the acquisition
        /// stays open and reported: never a guessed release, and never an unload
        /// of a root that is not bound to this scope.
        /// </para>
        /// </summary>
        /// <param name="runId">
        /// The Run the acquisition belongs to — the release dispatcher's own,
        /// which with the resource's <c>AcquiringStep</c> is the acquisition
        /// scope the ownership binding is keyed by.
        /// </param>
        internal static Result<Unit> Release(string runId, RunResource resource)
        {
            if (!string.Equals(resource.Kind, Kind, StringComparison.Ordinal))
            {
                return Result<Unit>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_API_004,
                    "The prefab-contents release handler received another resource kind."));
            }
            var guid = GuidOf(resource.Data);
            if (string.IsNullOrEmpty(guid))
            {
                return Result<Unit>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_API_004,
                    "The prefab-contents resource carries no Prefab guid."));
            }
            var path = AssetDatabase.GUIDToAssetPath(guid!);
            var scope = ScopeOf(runId, resource);
            var owned = PrefabContentsOwnership.FindOwned(scope, guid!, path);
            if (owned is null)
            {
                var roots = FindEditingRoots(path);
                if (roots.Count == 0)
                {
                    // Already released: nothing of this Prefab is open in this
                    // session, so there is no object left to discard, and the
                    // journaled acquisition still closes (SPEC §E6 item 4 —
                    // after a restart the release runs from the fold alone).
                    return Result<Unit>.Success(Unit.Value);
                }
                if (IsProvedUnloaded(scope, guid!, path))
                {
                    // This acquisition's own root was unloaded by its own
                    // earlier release; only the record is missing. Unloading
                    // nothing and reporting success is what lets the caller
                    // journal the pairing this acquisition is owed, so the
                    // still-bound acquisition of the same Prefab stays the one
                    // the fold retains — and it is released through its own
                    // scope, on its own root.
                    return Result<Unit>.Success(Unit.Value);
                }
                // Sessions of the Prefab are open that no binding ties to this
                // acquisition, so unloading one of them would destroy another
                // owner's unsaved editing session. The acquisition stays
                // journaled open and held — the fail-safe of the §E6 item 4
                // two-fact model — for review.
                return Result<Unit>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_API_004,
                    "The open Prefab contents cannot be attributed to this " +
                    "acquisition, so it cannot be released safely."));
            }
            var ownedId = PrefabContentsOwnership.SessionIdOf(owned!);
            try
            {
                PrefabUtility.UnloadPrefabContents(owned);
            }
            catch (Exception e)
            {
                // The acquisition stays journaled-open: an object this release
                // could not unload is never reported as released (SPEC §E6
                // item 4 two-fact model), and its binding stays so a retry can
                // still name the object it owns.
                return Result<Unit>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_API_004,
                    "The open Prefab contents could not be unloaded: " + e.GetType().Name));
            }
            // The proof of the unload is durable before the binding goes: from
            // here on the acquisition's absent binding always has its reason.
            PrefabContentsOwnership.RecordUnloaded(scope, guid!, ownedId);
            PrefabContentsOwnership.Unbind(scope, guid!);
            return Result<Unit>.Success(Unit.Value);
        }

        // True when the durable release evidence proves this scope unloaded its
        // own root and that root is not open any more — the two facts that make
        // an unbindable acquisition releasable without touching a single object.
        // The second fact is checked against this asset's live editing roots, so
        // an id that still names a live session is never treated as gone.
        private static bool IsProvedUnloaded(string scope, string guid, string assetPath)
        {
            if (string.IsNullOrEmpty(scope))
            {
                return false;
            }
            if (PrefabContentsOwnership.UnloadedOf(scope, guid) is not long unloadedId)
            {
                return false;
            }
            var roots = FindEditingRoots(assetPath);
            for (var i = 0; i < roots.Count; i++)
            {
                if (PrefabContentsOwnership.SessionIdOf(roots[i]) == unloadedId)
                {
                    return false;
                }
            }
            return true;
        }

        // The acquisition scope the release is named by: the Run id the
        // dispatcher was called with and the step of this resource's own
        // resource_acquired record, in the one form the acquiring step's
        // registry used when it bound the root it loaded. A record that carried
        // no step names no acquisition — its scope is the empty one no binding
        // can ever match, so such a release fails safe by construction.
        private static string ScopeOf(string runId, RunResource resource)
        {
            if (string.IsNullOrEmpty(runId) || resource.AcquiringStep is not int step)
            {
                return string.Empty;
            }
            return OpWeave.Core.Runtime.RunResourceScope.Of(runId, step);
        }
    }
}
