#nullable enable

using System;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The StepStarted snapshots of the two SPEC §G6 Prefab-content Operations,
    /// built and read on the adapter side of the boundary exactly as
    /// <see cref="PrefabContentFingerprint"/> builds the §G6.1 one: the values
    /// enter and leave as the immutable <see cref="JsonValue"/> of the single
    /// Canonical JSON implementation, so no JSON token type crosses into
    /// <c>OpWeave.Operations</c>, which deliberately holds no Newtonsoft.Json
    /// reference (SPEC §0.5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The member names are the local, documented evidence shape the §F5 table
    /// leaves to the implementation — <c>prefabGuid</c>/<c>prefabPath</c>/
    /// <c>contentsOpenAtPreflight</c> for the open row and §G6.1's own members
    /// plus <c>fingerprintUnavailable</c>/<c>targetBefore</c> for the save row —
    /// and both are additive-compatible: a snapshot written by an older Run
    /// keeps its meaning, an unreadable one is never guessed (the reader
    /// reports it and §F5 turns the verdict into Unknown).
    /// </para>
    /// </remarks>
    internal static class PrefabContentsSnapshots
    {
        /// <summary>The snapshot of a Preflight that resolved nothing and
        /// recorded nothing — an empty JSON object, never <c>null</c>.</summary>
        internal static JsonValue Empty { get; } =
            JsonValue.From(new JObject());

        // ── unity.prefab.open_contents ──────────────────────────────────────

        /// <summary>
        /// The <c>unity.prefab.open_contents</c> snapshot: the identity the
        /// step's <c>Acquire</c> journals — rebuilt from the same canonical
        /// values so the §E6 item 4 pairing matches — plus the artifact's own
        /// pre-execution state, how many contents of this Prefab were already
        /// open when the step began (§F5 common rule 4: content that predates
        /// the step is never its artifact).
        /// </summary>
        internal static JsonValue Open(
            string prefabGuid, string prefabPath, int contentsOpenAtPreflight) =>
            JsonValue.From(new JObject
            {
                ["prefabGuid"] = prefabGuid,
                ["prefabPath"] = prefabPath,
                ["contentsOpenAtPreflight"] = contentsOpenAtPreflight,
            });

        /// <summary>The open snapshot in the form the §F5 decision needs: the
        /// journaled identity plus the artifact's pre-execution state.</summary>
        internal sealed record OpenRecord(
            string Path, int? OpenAtPreflight, JsonValue ResourceData);

        /// <summary>
        /// The recorded identity and pre-execution state of an
        /// <c>open_contents</c> snapshot, or <see langword="null"/> when the
        /// snapshot carries neither a usable guid and path — the unreadable
        /// snapshot §F5 answers as Unknown rather than as a re-execution.
        /// </summary>
        internal static OpenRecord? ReadOpen(JsonValue? snapshot)
        {
            if (snapshot?.ToToken() is not JObject obj)
            {
                return null;
            }
            var guid = Text(obj, "prefabGuid");
            var path = Text(obj, "prefabPath");
            if (string.IsNullOrEmpty(guid) || string.IsNullOrEmpty(path))
            {
                return null;
            }
            int? open = null;
            if (obj["contentsOpenAtPreflight"] is JValue { Type: JTokenType.Integer } n &&
                n.Value is long count)
            {
                open = (int)count;
            }
            return new OpenRecord(
                path!, open, PrefabContentsResources.Data(guid!, path!));
        }

        // ── unity.prefab.save_contents ──────────────────────────────────────

        /// <summary>
        /// The <c>unity.prefab.save_contents</c> snapshot: §G6.1 item 4's
        /// expected fingerprint, <c>prefabFingerprintVersion</c>, the target,
        /// the Unity version and the serialization conditions when the content
        /// could be fingerprinted; the target, the versions and
        /// <c>fingerprintUnavailable</c> with the recorded reason when it could
        /// not (§G6.1's Unknown condition recorded, not hidden); and always the
        /// destination's own pre-execution fingerprint.
        /// </summary>
        internal static JsonValue Save(
            PrefabFingerprintResult expected, string targetGuid, string targetPath)
        {
            var snapshot = expected is PrefabFingerprintResult.Computed computed
                ? (JObject)PrefabContentFingerprint
                    .Snapshot(computed.Fingerprint, targetGuid, targetPath).ToToken()
                : new JObject
                {
                    ["prefabFingerprintVersion"] = PrefabContentFingerprint.CurrentVersion,
                    ["target"] = new JObject
                    {
                        ["guid"] = targetGuid,
                        ["path"] = targetPath,
                    },
                    ["unityVersion"] = Application.unityVersion,
                };
            if (expected is PrefabFingerprintResult.Unknown unavailable)
            {
                snapshot["fingerprintUnavailable"] = unavailable.Reason;
            }

            // §F5 common rule 4 needs the destination's state before this step
            // ran: content already equal to the expected fingerprint is not
            // this step's artifact.
            snapshot["targetBefore"] = DestinationBefore(targetPath);
            return JsonValue.From(snapshot);
        }

        /// <summary>True when the snapshot carries no JSON object at all — the
        /// unreadable record §F5 answers as Unknown, named separately from a
        /// readable snapshot that simply records no evidence.</summary>
        internal static bool IsUnreadable(JsonValue? snapshot) =>
            snapshot?.ToToken() is not JObject;

        /// <summary>The save destination the snapshot names, or
        /// <see langword="null"/> when it names none.</summary>
        internal static string? SavedTargetPath(JsonValue? snapshot) =>
            snapshot?.ToToken() is JObject obj &&
            obj["target"] is JObject target &&
            target["path"] is JValue { Type: JTokenType.String } path
                ? (string?)path.Value
                : null;

        /// <summary>
        /// True when the recorded pre-execution state already equals the content
        /// the step intended to save — including the fail-closed cases where no
        /// comparable pre-execution state was recorded.
        /// </summary>
        internal static bool SavedDestinationAlreadyHeldContent(JsonValue? snapshot)
        {
            if (snapshot?.ToToken() is not JObject snapshotObj ||
                snapshotObj["targetBefore"] is not JObject before)
            {
                return true;
            }
            if (before["absent"] is JValue { Type: JTokenType.Boolean, Value: true })
            {
                return false;
            }
            var recorded = (before["fingerprint"] as JValue)?.Value as string;
            var expected = (snapshotObj["expectedFingerprint"] as JValue)?.Value as string;
            return string.IsNullOrEmpty(recorded) || string.IsNullOrEmpty(expected) ||
                string.Equals(recorded, expected, StringComparison.Ordinal);
        }

        // The destination's content state before the step ran: absent, its
        // §G6.1 fingerprint, or why no fingerprint exists.
        private static JObject DestinationBefore(string path)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
            {
                return new JObject { ["absent"] = true };
            }
            var before = PrefabContentFingerprint.ComputeAsset(path);
            return before is PrefabFingerprintResult.Computed computed
                ? new JObject { ["fingerprint"] = computed.Fingerprint.Hash }
                : new JObject
                {
                    ["unavailable"] = ((PrefabFingerprintResult.Unknown)before).Reason,
                };
        }

        private static string? Text(JObject obj, string name) =>
            obj[name] is JValue { Type: JTokenType.String } value
                ? (string?)value.Value
                : null;
    }
}
