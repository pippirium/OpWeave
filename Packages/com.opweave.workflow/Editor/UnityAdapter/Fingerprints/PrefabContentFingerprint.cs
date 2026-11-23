#nullable enable

using System;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The PrefabContentFingerprint v1 entry point (SPEC §G6.1, §0.6 SG-12,
    /// §F5): computes the deterministic structural fingerprint of Prefab
    /// content, forms the StepStarted snapshot used by Preflight, and
    /// compares a stored snapshot against live content at recovery time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Prefab content identity is proven by this fingerprint alone — never
    /// by <c>AssetDatabase.GetAssetDependencyHash</c>, path or timestamps
    /// (SPEC §G6.1 禁止, §F5). When a fingerprint cannot be computed or
    /// compared, the outcome is Unknown; automatic Completed stays
    /// disabled for every state L-15 has not proven (SPEC §T.4 L-15,
    /// §F5 共通規則). A content root that is itself a Prefab instance —
    /// Variant content — is normalized through its source-Prefab
    /// reference and persisted override set (scene-side entries Unity
    /// never serializes, like <c>m_RootOrder</c>, are excluded);
    /// sub-cases the walk cannot prove — unconnected or missing-source
    /// instances, dangling references — still fail closed into Unknown.
    /// </para>
    /// <para>
    /// <b>Local, reversible choices.</b> The snapshot's member names
    /// (<c>prefabFingerprintVersion</c>, <c>expectedFingerprint</c>,
    /// <c>target</c>, <c>unityVersion</c>, <c>serialization</c>) are the
    /// evidence shape the F5 table leaves to implementation; the
    /// comparability conditions are exactly SPEC §G6.1's — same
    /// fingerprint format version, same Unity version and the same
    /// serialization conditions. <c>serialization</c> records the
    /// project's asset-serialization settings readable through public
    /// <see cref="EditorSettings"/> members
    /// (<see cref="EditorSettings.serializationMode"/>,
    /// <see cref="EditorSettings.assetPipelineMode"/>,
    /// <see cref="EditorSettings.serializeInlineMappingsOnOneLine"/>);
    /// a different, missing or unparseable recorded condition fails
    /// closed into Unknown, because identical persisted content can no
    /// longer be guaranteed (SPEC §G6.1 Unknown 条件).
    /// </para>
    /// </remarks>
    public static class PrefabContentFingerprint
    {
        /// <summary>The SPEC §G6.1 fingerprint format version implemented here.</summary>
        public const int CurrentVersion = 1;

        /// <summary>
        /// Fingerprints the content rooted at <paramref name="contentRoot"/>
        /// — a Prefab contents editing root (the Preflight input of
        /// <c>unity.prefab.save_contents</c>, SPEC §G6 item 5), a source
        /// GameObject whose tree becomes the saved Prefab, or a saved
        /// Prefab asset's root. Read-only (SPEC §G6.1 item 1).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="contentRoot"/> is null or destroyed.</exception>
        public static PrefabFingerprintResult Compute(GameObject contentRoot)
        {
            if (contentRoot == null)
            {
                throw new ArgumentNullException(nameof(contentRoot));
            }
            return PrefabFingerprintNormalizer.Build(contentRoot);
        }

        /// <summary>
        /// The Preflight read of SPEC §G6.1 item 1: fingerprints the
        /// to-be-saved content rooted at <paramref name="contentRoot"/>
        /// as it will be persisted at <paramref name="destinationPath"/>.
        /// <c>SaveAsPrefabAsset</c> names the saved root after the
        /// destination file, so the document is computed with the
        /// root's name normalized to the destination file name (without
        /// extension) — the scene-side root name never enters the
        /// fingerprint. Read-only, like <see cref="Compute"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="contentRoot"/> is null or destroyed, or
        /// <paramref name="destinationPath"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="destinationPath"/> is empty or has no usable file name.
        /// </exception>
        public static PrefabFingerprintResult ComputeForSave(
            GameObject contentRoot, string destinationPath)
        {
            if (contentRoot == null)
            {
                throw new ArgumentNullException(nameof(contentRoot));
            }
            if (destinationPath is null)
            {
                throw new ArgumentNullException(nameof(destinationPath));
            }
            var persistedName = FileStem(destinationPath);
            if (persistedName.Length == 0)
            {
                throw new ArgumentException(
                    "The destination path has no usable file name.",
                    nameof(destinationPath));
            }
            return PrefabFingerprintNormalizer.Build(contentRoot, persistedName);
        }

        // The file name Unity gives the saved root — the destination
        // asset's base name without its extension. Asset paths use '/'
        // separators; a stray '\' is tolerated the same way.
        private static string FileStem(string assetPath)
        {
            var name = assetPath;
            var slash = name.LastIndexOf('/');
            var back = name.LastIndexOf('\\');
            var cut = slash > back ? slash : back;
            if (cut >= 0)
            {
                name = name.Substring(cut + 1);
            }
            var dot = name.LastIndexOf('.');
            if (dot > 0)
            {
                name = name.Substring(0, dot);
            }
            return name;
        }

        /// <summary>
        /// Fingerprints a saved Prefab asset at <paramref name="assetPath"/>
        /// — the recovery-time read of SPEC §G6.1 item 6. The asset is
        /// loaded through <see cref="PrefabUtility.LoadPrefabContents"/>,
        /// fingerprinted by the same rules as Preflight content, then
        /// unloaded.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="assetPath"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="assetPath"/> is empty.</exception>
        public static PrefabFingerprintResult ComputeAsset(string assetPath)
        {
            if (assetPath is null)
            {
                throw new ArgumentNullException(nameof(assetPath));
            }
            if (assetPath.Length == 0)
            {
                throw new ArgumentException("The asset path must not be empty.", nameof(assetPath));
            }

            GameObject contents;
            try
            {
                contents = PrefabUtility.LoadPrefabContents(assetPath);
            }
            catch (Exception)
            {
                return new PrefabFingerprintResult.Unknown(
                    "the prefab contents at '" + assetPath + "' could not be loaded");
            }
            if (contents == null)
            {
                return new PrefabFingerprintResult.Unknown(
                    "the prefab contents at '" + assetPath + "' could not be loaded");
            }
            try
            {
                return Compute(contents);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// <summary>
        /// The StepStarted snapshot of SPEC §G6.1 item 4: the expected
        /// fingerprint, <c>prefabFingerprintVersion: 1</c>, the target
        /// (save-destination guid and/or path — absent keys are omitted,
        /// never serialized as null), the Editor's Unity version and the
        /// project's serialization conditions compared by
        /// <see cref="Check"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="expected"/> is null.</exception>
        public static JsonValue Snapshot(
            PrefabFingerprint expected, string? targetGuid, string? targetPath)
        {
            if (expected is null)
            {
                throw new ArgumentNullException(nameof(expected));
            }
            var target = new JObject();
            if (targetGuid is not null)
            {
                target["guid"] = targetGuid;
            }
            if (targetPath is not null)
            {
                target["path"] = targetPath;
            }
            return JsonValue.From(new JObject
            {
                ["expectedFingerprint"] = expected.Hash,
                ["prefabFingerprintVersion"] = expected.Version,
                ["serialization"] = SerializationConditions(),
                ["target"] = target,
                ["unityVersion"] = Application.unityVersion,
            });
        }

        // The serialization conditions of SPEC §G6.1 item 4, read through
        // public Editor settings only: the project's asset serialization
        // mode, asset pipeline mode and inline-mapping layout. Compared
        // member-by-member in Check; these members sit outside the hashed
        // fingerprint document itself.
        private static JObject SerializationConditions() => new JObject
        {
            ["assetPipelineMode"] = EditorSettings.assetPipelineMode.ToString(),
            ["serializationMode"] = EditorSettings.serializationMode.ToString(),
            ["serializeInlineMappingsOnOneLine"] =
                EditorSettings.serializeInlineMappingsOnOneLine,
        };

        /// <summary>
        /// Compares a stored fingerprint snapshot against the live content
        /// rooted at <paramref name="contentRoot"/> (SPEC §G6.1 items 6–7).
        /// <see cref="PrefabFingerprintCheck.Matched"/> requires the same
        /// format version, the same Unity version, the same recorded
        /// serialization conditions and an identical fingerprint;
        /// everything else is <see cref="PrefabFingerprintCheck.Mismatched"/>
        /// (a provably different content) or
        /// <see cref="PrefabFingerprintCheck.Unknown"/> (no comparable
        /// evidence — different/unknown version, Unity version mismatch,
        /// a differing, missing or unparseable serialization condition,
        /// malformed snapshot, or content that cannot be fingerprinted).
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="snapshot"/> is null, or <paramref name="contentRoot"/> is null or destroyed.
        /// </exception>
        public static PrefabFingerprintCheck Check(JsonValue snapshot, GameObject contentRoot)
        {
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }
            if (contentRoot == null)
            {
                throw new ArgumentNullException(nameof(contentRoot));
            }

            if (snapshot.ToToken() is not JObject obj)
            {
                return new PrefabFingerprintCheck.Unknown(
                    "the fingerprint snapshot is not a JSON object");
            }
            var version = obj["prefabFingerprintVersion"] as JValue;
            if (version?.Value is not long v || v != CurrentVersion)
            {
                return new PrefabFingerprintCheck.Unknown(
                    "prefabFingerprintVersion differs from or is not " + CurrentVersion);
            }
            var snapshotUnity = obj["unityVersion"] as JValue;
            var snapshotUnityVersion = snapshotUnity?.Value as string;
            if (string.IsNullOrEmpty(snapshotUnityVersion))
            {
                return new PrefabFingerprintCheck.Unknown(
                    "the fingerprint snapshot carries no Unity version");
            }
            if (!string.Equals(
                snapshotUnityVersion, Application.unityVersion, StringComparison.Ordinal))
            {
                return new PrefabFingerprintCheck.Unknown(
                    "the Unity version differs from the snapshot's " + snapshotUnityVersion);
            }
            if (obj["serialization"] is not JObject serialization)
            {
                return new PrefabFingerprintCheck.Unknown(
                    "the fingerprint snapshot carries no serialization conditions");
            }
            var conditions = SerializationConditions();
            foreach (var currentMember in conditions.Properties())
            {
                var recorded = serialization[currentMember.Name];
                if (!JToken.DeepEquals(recorded, currentMember.Value))
                {
                    return new PrefabFingerprintCheck.Unknown(
                        "the serialization condition '" + currentMember.Name +
                        "' is missing or differs from the snapshot's " +
                        (recorded?.ToString() ?? "<missing>"));
                }
            }
            foreach (var recordedMember in serialization.Properties())
            {
                if (conditions[recordedMember.Name] is null)
                {
                    return new PrefabFingerprintCheck.Unknown(
                        "the fingerprint snapshot records an unrecognized " +
                        "serialization condition '" + recordedMember.Name + "'");
                }
            }
            var expected = obj["expectedFingerprint"] as JValue;
            if (expected?.Value is not string expectedHash || expectedHash.Length == 0)
            {
                return new PrefabFingerprintCheck.Unknown(
                    "the fingerprint snapshot carries no expectedFingerprint");
            }

            var actual = Compute(contentRoot);
            if (actual is PrefabFingerprintResult.Unknown unknown)
            {
                return new PrefabFingerprintCheck.Unknown(unknown.Reason);
            }
            var fingerprint = ((PrefabFingerprintResult.Computed)actual).Fingerprint;
            return string.Equals(fingerprint.Hash, expectedHash, StringComparison.Ordinal)
                ? (PrefabFingerprintCheck)new PrefabFingerprintCheck.Matched()
                : new PrefabFingerprintCheck.Mismatched();
        }
    }
}
