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
    /// The shared SPEC §F5/§G6 evidence helpers of the three Prefab-creating
    /// Operations (SPEC §I.2: <c>unity.prefab.create_from_gameobject</c>,
    /// <c>unity.prefab.instantiate</c>, <c>unity.prefab.create_variant</c>):
    /// the <c>.prefab</c> write-path and destination checks, the recorded
    /// path-before / fingerprint evidence members, the occupied-path probe,
    /// the placed-instance override walk of the <c>instantiate</c> row and
    /// the source-connection check of the <c>create_from_gameobject</c> row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fingerprint evidence members reuse the §G6.1 snapshot shape of
    /// <see cref="PrefabContentFingerprint.Snapshot"/> verbatim — a computed
    /// expectation is stored through it, an uncomputable one is recorded as
    /// the same <c>prefabFingerprintVersion</c>/<c>unityVersion</c> pair plus
    /// <c>fingerprintUnavailable</c> — so a stored member always feeds
    /// <see cref="PrefabContentFingerprint.Check"/> unchanged: missing,
    /// malformed or unavailable evidence answers Unknown, never a cheaper
    /// comparison (SPEC §G6.1 禁止, §F5 common rule 6).
    /// </para>
    /// </remarks>
    internal static class PrefabCreateEvidence
    {
        /// <summary>The §I.2 <c>AssetPath(.prefab)</c> extension.</summary>
        internal const string PrefabExtension = ".prefab";

        // The placement property paths a freshly instantiated Prefab
        // instance root legitimately records — the "position の指定"
        // override family of the instantiate evidence row. The only other
        // tolerated record is the m_Name Unity itself stamps on the root's
        // source GameObject at instantiation, and only while that stamp
        // still carries the Prefab's own root name. Every other recorded
        // override (a renamed root, a child property, a scale change) is
        // evidence the artifact was modified after creation — and so is a
        // live value the record fails to reflect, which the root's own
        // live checks below cover.
        private static readonly HashSet<string> s_placementPaths =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z",
                "m_LocalRotation.x", "m_LocalRotation.y",
                "m_LocalRotation.z", "m_LocalRotation.w",
                "m_RootOrder",
                "m_LocalEulerAnglesHint.x", "m_LocalEulerAnglesHint.y",
                "m_LocalEulerAnglesHint.z",
            };

        /// <summary>
        /// The write-path half of a <c>.prefab</c> destination: the §G7
        /// item 1 shape and read-only-package refusals, then the §I.2
        /// extension requirement — both OW-UNITY-005 ("invalid path").
        /// </summary>
        internal static Result<string> CheckPrefabWritePath(string? raw, string port)
        {
            var write = AssetPathPolicy.CheckWritePath(raw, port);
            if (!write.Ok)
            {
                return write;
            }
            return write.Value!.EndsWith(
                PrefabExtension, StringComparison.OrdinalIgnoreCase)
                ? write
                : Result<string>.Failure(InvalidPath(
                    port,
                    "The asset path of port '" + port +
                        "' must carry the '.prefab' extension.",
                    "A Prefab is serialized in the native .prefab format."));
        }

        /// <summary>
        /// The destination checks of the create contract: the parent
        /// folder must exist, and an occupied path refuses with
        /// OW-UNITY-005 unless <paramref name="overwrite"/> is true —
        /// and then only when the occupied target is itself an existing
        /// Prefab asset (a directory, an orphan .meta, a database entry
        /// or a foreign asset type is never rewritten).
        /// </summary>
        internal static Diagnostic? CheckDestination(
            string path, string port, bool overwrite)
        {
            var parent = AssetArtifact.ParentFolder(path);
            var physical = AssetArtifact.ToPhysicalPath(path);
            if (physical is null || parent is null ||
                !AssetDatabase.IsValidFolder(parent))
            {
                return InvalidPath(
                    port,
                    "The target folder does not exist.",
                    "Create the folder first, or choose a folder that exists.");
            }
            if (!AssetArtifact.Occupied(physical, path))
            {
                return null;
            }
            if (!overwrite)
            {
                return OccupiedPath(port);
            }
            return IsExistingPrefabAsset(physical, path)
                ? null
                : OccupiedNotPrefab(port);
        }

        /// <summary>Whether the occupied path names an existing imported
        /// Prefab asset — the only artifact <c>overwrite</c> may rewrite.</summary>
        internal static bool IsExistingPrefabAsset(string physical, string path) =>
            File.Exists(physical) &&
            !Directory.Exists(physical) &&
            AssetDatabase.LoadAssetAtPath<GameObject>(path) is { } main &&
            PrefabUtility.GetPrefabAssetType(main) != PrefabAssetType.NotAPrefab;

        /// <summary>Whether anything occupies <paramref name="assetPath"/> —
        /// the §F5 "path に何も無い" state probe (file, directory, orphan
        /// .meta or database entry all count).</summary>
        internal static bool OccupiedNow(string assetPath)
        {
            var physical = AssetArtifact.ToPhysicalPath(assetPath);
            return physical is not null && AssetArtifact.Occupied(physical, assetPath) ||
                physical is null;
        }

        /// <summary>
        /// The recorded pre-execution state of the destination path —
        /// <c>{"absent":true}</c> when nothing occupied it, else the
        /// existing asset's guid and its PrefabContentFingerprint v1 (or
        /// why the fingerprint cannot be computed). §F5 common rule 4: a
        /// pre-existing same-name artifact is never the step's product.
        /// </summary>
        internal static JObject PathBeforeState(string path)
        {
            var physical = AssetArtifact.ToPhysicalPath(path);
            if (physical is null || !AssetArtifact.Occupied(physical, path))
            {
                return new JObject { ["absent"] = true };
            }
            var before = new JObject
            {
                ["guid"] = AssetDatabase.AssetPathToGUID(
                    path, AssetPathToGUIDOptions.OnlyExistingAssets),
            };
            var fingerprint = PrefabContentFingerprint.ComputeAsset(path);
            if (fingerprint is PrefabFingerprintResult.Computed computed)
            {
                before["fingerprint"] = computed.Fingerprint.Hash;
            }
            else
            {
                before["fingerprintUnavailable"] =
                    ((PrefabFingerprintResult.Unknown)fingerprint).Reason;
            }
            return before;
        }

        /// <summary>
        /// The fingerprint evidence member recorded in a snapshot: the
        /// §G6.1 item 4 snapshot shape when the content normalized, or the
        /// same version markers plus the recorded Unknown reason when it
        /// did not (the reason is recorded, not hidden — the recovery then
        /// answers Unknown rather than comparing less).
        /// </summary>
        internal static JsonValue FingerprintEvidence(
            PrefabFingerprintResult result, string? guid, string? path)
        {
            if (result is PrefabFingerprintResult.Computed computed)
            {
                return PrefabContentFingerprint.Snapshot(
                    computed.Fingerprint, guid, path);
            }
            var target = new JObject();
            if (guid is not null)
            {
                target["guid"] = guid;
            }
            if (path is not null)
            {
                target["path"] = path;
            }
            return JsonValue.From(new JObject
            {
                ["prefabFingerprintVersion"] = PrefabContentFingerprint.CurrentVersion,
                ["unityVersion"] = Application.unityVersion,
                ["fingerprintUnavailable"] =
                    ((PrefabFingerprintResult.Unknown)result).Reason,
                ["target"] = target,
            });
        }

        /// <summary>
        /// Compares a recorded fingerprint evidence member against the
        /// live content rooted at <paramref name="contentRoot"/> (SPEC
        /// §G6.1 items 6–7). Missing or malformed evidence is Unknown.
        /// </summary>
        internal static PrefabFingerprintCheck CheckFingerprint(
            JToken? recorded, GameObject contentRoot)
        {
            if (recorded is not JObject evidence)
            {
                return new PrefabFingerprintCheck.Unknown(
                    "the recorded fingerprint evidence is missing or malformed");
            }
            return PrefabContentFingerprint.Check(
                JsonValue.From(evidence), contentRoot);
        }

        /// <summary>
        /// The recovery-time read of a saved Prefab asset at
        /// <paramref name="assetPath"/>: loads its contents, runs the
        /// fingerprint check against the recorded evidence and unloads
        /// them (§G6.1 item 6).
        /// </summary>
        internal static PrefabFingerprintCheck CheckSavedAsset(
            JToken? recorded, string assetPath)
        {
            GameObject contents;
            try
            {
                contents = PrefabUtility.LoadPrefabContents(assetPath);
            }
            catch (Exception e)
            {
                return new PrefabFingerprintCheck.Unknown(
                    "the prefab contents could not be loaded: " + e.GetType().Name);
            }
            if (contents == null)
            {
                return new PrefabFingerprintCheck.Unknown(
                    "the prefab contents could not be loaded");
            }
            try
            {
                return CheckFingerprint(recorded, contents);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// <summary>
        /// The recorded source Prefab state of a scene GameObject — the
        /// §F5 row's "作成元の Prefab 状態": whether it is an outermost
        /// Prefab instance root, and the asset path and guid of its
        /// nearest Prefab source (empty strings when it is not one).
        /// </summary>
        internal static JObject SourcePrefabState(GameObject source)
        {
            var sourcePath = NormalizedInstancePath(source);
            return new JObject
            {
                ["outermostInstanceRoot"] =
                    PrefabUtility.IsOutermostPrefabInstanceRoot(source),
                ["assetPath"] = sourcePath,
                ["assetGuid"] = sourcePath.Length == 0
                    ? string.Empty
                    : AssetDatabase.AssetPathToGUID(sourcePath),
            };
        }

        /// <summary>
        /// Whether <paramref name="go"/> is an outermost Prefab instance
        /// root whose source asset is exactly <paramref name="assetPath"/>
        /// — the connect half of the <c>create_from_gameobject</c>
        /// Completed condition.
        /// </summary>
        internal static bool IsOutermostInstanceOf(GameObject? go, string assetPath) =>
            go is not null &&
            PrefabUtility.IsOutermostPrefabInstanceRoot(go) &&
            string.Equals(
                NormalizedInstancePath(go), assetPath, StringComparison.Ordinal);

        /// <summary>The normalized (/-separated) asset path of the Prefab
        /// <paramref name="go"/> is the nearest instance root of — empty
        /// when it is not a Prefab instance.</summary>
        internal static string NormalizedInstancePath(GameObject go) =>
            (PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go) ?? string.Empty)
                .Replace('\\', '/');

        /// <summary>
        /// The instantiate row's "Override が position の指定以外に無い":
        /// every property modification the instance records must sit on
        /// the corresponding source of the instance root's Transform and
        /// name a placement property — or be the <c>m_Name</c> stamp Unity
        /// records on the root's source GameObject when an instance is
        /// created, which stays placement-neutral only while the stamped
        /// value is still the Prefab's own root name (a later rename
        /// rewrites the recorded value and is evidence again). The
        /// recorded value is evidence, not proof: the live instance root
        /// must still carry the source root's own name and scale, because
        /// a fresh instance inherits both — a rename or rescale is a
        /// post-creation modification whether or not the recorded
        /// override values reflect it. No component or GameObject may be
        /// added or removed inside the instance either. Returns the
        /// reason the evidence fails — fail-closed into
        /// <see cref="ReconcileResult.Unknown"/> — or
        /// <see langword="null"/> when the placement set is complete and
        /// exclusive.
        /// </summary>
        internal static string? UnexpectedInstanceOverride(GameObject instanceRoot)
        {
            var prefabInstance = PrefabUtility.GetPrefabInstanceHandle(instanceRoot);
            if (prefabInstance == null)
            {
                return "the object is not a Prefab instance";
            }
            var sourceRootTransform =
                PrefabUtility.GetCorrespondingObjectFromSource(instanceRoot.transform);
            if (sourceRootTransform == null)
            {
                return "the instance root's source Transform could not be resolved";
            }
            var sourceRoot =
                PrefabUtility.GetCorrespondingObjectFromSource(instanceRoot);
            if (sourceRoot == null)
            {
                return "the instance root's source GameObject could not be resolved";
            }
            // The live object is the authoritative evidence: a fresh
            // instance inherits the source root's name and scale, so a
            // live difference is a post-creation modification whether or
            // not the recorded override values reflect it — the recorded
            // m_Name value does not reliably track a live rename.
            if (!string.Equals(
                instanceRoot.name, sourceRoot.name, StringComparison.Ordinal))
            {
                return "the instance root was renamed";
            }
            if (!GameObjectEvidence.SameVector(
                sourceRoot.transform.localScale,
                instanceRoot.transform.localScale))
            {
                return "the instance root's scale differs from the Prefab's own";
            }
            SerializedObject serialized;
            try
            {
                serialized = new SerializedObject(prefabInstance);
            }
            catch (Exception)
            {
                return "the instance override state could not be read";
            }
            var modifications = serialized.FindProperty(
                "m_Modification.m_Modifications");
            if (modifications == null || !modifications.isArray)
            {
                return "the instance override list could not be read";
            }
            for (var i = 0; i < modifications.arraySize; i++)
            {
                var element = modifications.GetArrayElementAtIndex(i);
                var target = element.FindPropertyRelative("target")?.objectReferenceValue;
                var propertyPath =
                    element.FindPropertyRelative("propertyPath")?.stringValue;
                var allowed = propertyPath is not null &&
                    (target == sourceRootTransform
                        ? s_placementPaths.Contains(propertyPath)
                        : target == sourceRoot && propertyPath == "m_Name" &&
                            string.Equals(
                                element.FindPropertyRelative("value")?.stringValue,
                                sourceRoot.name, StringComparison.Ordinal));
                if (!allowed)
                {
                    return "an override beyond the placement is recorded (" +
                        (propertyPath ?? "<unreadable>") + ")";
                }
            }
            var removedComponents = serialized.FindProperty(
                "m_Modification.m_RemovedComponents");
            if (removedComponents != null && removedComponents.isArray &&
                removedComponents.arraySize != 0)
            {
                return "a removed-component override is recorded";
            }
            var removedObjects = serialized.FindProperty(
                "m_Modification.m_RemovedGameObjects");
            if (removedObjects != null && removedObjects.isArray &&
                removedObjects.arraySize != 0)
            {
                return "a removed-GameObject override is recorded";
            }
            var pending = new Stack<Transform>();
            pending.Push(instanceRoot.transform);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                for (var i = 0; i < current.childCount; i++)
                {
                    var child = current.GetChild(i);
                    if (PrefabUtility.IsAddedGameObjectOverride(child.gameObject))
                    {
                        return "an added-GameObject override is recorded";
                    }
                    pending.Push(child);
                }
                foreach (var component in current.GetComponents<Component>())
                {
                    if (component != null &&
                        PrefabUtility.IsAddedComponentOverride(component))
                    {
                        return "an added-component override is recorded";
                    }
                }
            }
            return null;
        }

        // ── Diagnostics ─────────────────────────────────────────────────

        /// <summary>The OW-UNITY-005 "invalid path" refusal of a destination
        /// that fails its shape or precondition.</summary>
        internal static Diagnostic InvalidPath(
            string port, string message, string hint) =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_005, message, port: port, hint: hint);

        /// <summary>The OW-UNITY-005 refusal of an already occupied path.</summary>
        internal static Diagnostic OccupiedPath(string port) =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_005,
                "The target asset already exists and is never overwritten.",
                port: port,
                hint: "Choose a path that is free, or pass 'overwrite': true.");

        /// <summary>The OW-UNITY-005 refusal of an overwrite target that is
        /// not an existing Prefab asset.</summary>
        internal static Diagnostic OccupiedNotPrefab(string port) =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_005,
                "The occupied target is not an existing Prefab asset, so " +
                    "'overwrite' cannot replace it.",
                port: port,
                hint: "Choose a path that is free, or name an existing .prefab asset.");

        /// <summary>The OW-UNITY-008 failure of a Prefab write or instance
        /// production Unity could not perform on an otherwise valid input.</summary>
        internal static Diagnostic WriteFailed(string port, string detail) =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_008,
                "The Prefab asset could not be produced: " + detail,
                port: port);
    }
}
