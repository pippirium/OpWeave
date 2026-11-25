#nullable enable

using System;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;

namespace OpWeave.Operations
{
    /// <summary>
    /// The shared helpers of the SPEC §I.2 Prefab-instance Operations:
    /// resolution of the required <c>unity.PrefabInstanceRef</c> input into
    /// the live outermost instance root, and the §F5 evidence pieces the
    /// Reconcilable <c>unity.prefab.unpack</c> writes at Preflight and reads
    /// back at Reconcile — the ordered depth-first structure entries of the
    /// instance hierarchy and the instance's own override list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One structure entry records the object's session handle, name,
    /// parent's session handle, ordered component entries
    /// (<see cref="ComponentEvidence"/> — type class-id, handle and
    /// normalized serialized content) and its prefab-membership state
    /// (own-instance-root flag and nearest source asset path). The session
    /// handles survive a domain reload but not an Editor restart, exactly
    /// what SPEC §F5 common rule 3's "same session" condition needs; a
    /// component whose content cannot be normalized is recorded without
    /// one, which comparisons treat as unverifiable — fail closed, never
    /// "unchanged". The parent handle is what makes the flat list a
    /// hierarchy: sibling children A,B and B nested under A share one
    /// depth-first order, so parentage is the only evidence that tells
    /// those configurations apart (SPEC §F5 requires the hierarchy
    /// configuration to match).
    /// </para>
    /// </remarks>
    internal static class PrefabInstanceEvidence
    {
        // The marker strings <see cref="OverridesOf"/> and
        // <see cref="NormalizeMember"/> record where the session could
        // not inspect a member. Together with
        // <see cref="ComponentEvidence.MissingComponentMarker"/> they are
        // the unverifiable markers of the evidence JSON: evidence-shaped
        // tokens that carry no inspected content, which a comparison must
        // never treat as equal — not even to an identical marker
        // (fail closed, Issue #313).
        private const string UnreadablePrefix = "<unreadable:";
        private const string UnsupportedPrefix = "<unsupported:";

        /// <summary>
        /// Resolves the required Prefab-instance input port to the live
        /// outermost instance root — §G2 resolution diagnostics pass
        /// through, a dead reference is OW-UNITY-001 and a resolvable
        /// object that is not an outermost Prefab instance root is
        /// OW-UNITY-004 (the SPEC §J "Prefab asset vs instance" mix-up).
        /// </summary>
        internal static Result<GameObject> ResolveInstance(StepContext ctx, string port)
        {
            GameObject? root;
            try
            {
                root = ctx.Get<GameObject>(port);
            }
            catch (StepContextException failure)
            {
                return Result<GameObject>.Failure(failure.Diagnostics);
            }
            if (root == null)
            {
                return Result<GameObject>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_001,
                    "The referenced Prefab instance no longer exists.",
                    port: port));
            }
            if (!PrefabUtility.IsOutermostPrefabInstanceRoot(root))
            {
                return Result<GameObject>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_004,
                    "The referenced GameObject is not an outermost Prefab instance root.",
                    port: port));
            }
            return Result<GameObject>.Success(root);
        }

        /// <summary>One ordered structure entry of a hierarchy object: its
        /// session handle, name, parent's session handle, ordered component
        /// evidence, own serialized state and prefab-membership state.</summary>
        internal static JObject StructureEntry(GameObject go)
        {
            var entry = new JObject
            {
                ["handle"] = SessionHandle.Write(go),
                ["name"] = go.name,
                ["parent"] = go.transform.parent is { } parent
                    ? SessionHandle.Write(parent.gameObject)
                    : JValue.CreateNull(),
                ["components"] = ComponentsOf(go),
                ["prefab"] = new JObject
                {
                    ["instanceRoot"] = PrefabUtility.IsAnyPrefabInstanceRoot(go),
                    ["assetPath"] = PrefabCreateEvidence.NormalizedInstancePath(go),
                },
            };
            // The GameObject's own serialized state (active flag, layer,
            // tag, static flags) — the m_Component link array is covered by
            // the "components" member. A foreign SetActive is evidence too.
            // A GameObject whose serialized state cannot be normalized is
            // recorded without a "state" member — unverifiable, never
            // unchanged (fail closed).
            if (SerializedFieldsNormalizer.TryNormalize(
                    go, p => p.name == "m_Component" && p.isArray,
                    out var state, out _) &&
                state is not null)
            {
                entry["state"] = state;
            }
            return entry;
        }

        /// <summary>The ordered depth-first structure entries of the whole
        /// hierarchy rooted at <paramref name="root"/> — the instance's
        /// complete shape as SPEC §F5's unpack row records it.</summary>
        internal static JArray StructureOf(GameObject root)
        {
            var entries = new JArray();
            Walk(root.transform, entries);
            return entries;
        }

        /// <summary>
        /// The instance root's own override list — the serialized
        /// <c>m_Modification</c> members of the PrefabInstance handle
        /// (property modifications, removed components and GameObjects,
        /// the transform parent), normalized member by member so object
        /// references resolve to persistent guid · localFileId keys (or
        /// session handles for scene objects). The recorded order is kept
        /// as serialized: both sides read the same list, and a foreign
        /// reorder is itself a modification.
        /// </summary>
        internal static JToken OverridesOf(GameObject instanceRoot)
        {
            var result = new JArray();
            var handle = PrefabUtility.GetPrefabInstanceHandle(instanceRoot);
            if (handle is null)
            {
                return JValue.CreateString(
                    UnreadablePrefix + " not a Prefab instance>");
            }
            SerializedObject serialized;
            try
            {
                serialized = new SerializedObject(handle);
            }
            catch (Exception e)
            {
                return JValue.CreateString(
                    UnreadablePrefix + " " + e.GetType().Name + ">");
            }
            var modification = serialized.FindProperty("m_Modification");
            if (modification is null)
            {
                return JValue.CreateString(
                    UnreadablePrefix + " no m_Modification>");
            }
            var iterator = modification.Copy();
            var end = iterator.GetEndProperty();
            var depth = modification.depth;
            if (!iterator.Next(enterChildren: true))
            {
                return result;
            }
            while (iterator.depth > depth &&
                !SerializedProperty.EqualContents(iterator, end))
            {
                result.Add(new JObject
                {
                    ["member"] = iterator.name,
                    ["data"] = NormalizeMember(iterator),
                });
                if (!iterator.Next(enterChildren: false))
                {
                    break;
                }
            }
            return result;
        }

        /// <summary>
        /// Whether the live hierarchy rooted at <paramref name="liveRoot"/>
        /// equals the recorded structure entries under the requested unpack
        /// mode — same objects (session handles), same names, same parents,
        /// same component evidence, and the membership the mode leaves
        /// behind: after
        /// <paramref name="completely"/> no entry may sit inside a Prefab
        /// instance, after the outermost-only unpack the recorded members of
        /// the target instance are freed while deeper instances keep their
        /// recorded state. <paramref name="reason"/> names the first failing
        /// sub-check so the caller's
        /// <see cref="ReconcileResult.Unknown"/> stays diagnosable.
        /// </summary>
        internal static bool StructuresMatch(
            JToken? recorded, GameObject liveRoot, bool completely,
            string targetAssetPath, out string reason)
        {
            reason = string.Empty;
            if (recorded is not JArray entries)
            {
                reason = "the recorded structure evidence is missing or malformed";
                return false;
            }
            var live = StructureOf(liveRoot);
            if (live.Count != entries.Count)
            {
                reason = "the hierarchy holds a different number of objects";
                return false;
            }
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i] is not JObject expected ||
                    live[i] is not JObject actual)
                {
                    reason = "the recorded structure evidence is malformed";
                    return false;
                }
                if (!JToken.DeepEquals(expected["handle"], actual["handle"]))
                {
                    reason = "an object in the hierarchy is not the recorded one";
                    return false;
                }
                if (!JToken.DeepEquals(expected["name"], actual["name"]))
                {
                    reason = "an object in the hierarchy was renamed";
                    return false;
                }
                if (!JToken.DeepEquals(expected["parent"], actual["parent"]))
                {
                    reason = "an object in the hierarchy was reparented";
                    return false;
                }
                var expectedComponents = expected["components"];
                var actualComponents = actual["components"];
                if (!ComponentsVerifiable(expectedComponents) ||
                    !ComponentsVerifiable(actualComponents))
                {
                    reason = "an object's component evidence could not be inspected";
                    return false;
                }
                if (!JToken.DeepEquals(expectedComponents, actualComponents))
                {
                    reason = "an object's component composition differs from the snapshot";
                    return false;
                }
                var expectedState = expected["state"];
                var actualState = actual["state"];
                if (expectedState is null || actualState is null)
                {
                    reason = "an object's serialized state could not be inspected";
                    return false;
                }
                if (!JToken.DeepEquals(expectedState, actualState))
                {
                    reason = "an object's serialized state differs from the snapshot";
                    return false;
                }
                var recordedPath =
                    (expected["prefab"] as JObject)?["assetPath"]
                        is JValue { Type: JTokenType.String } rp
                        ? (string?)rp.Value
                        : null;
                var actualPrefab = actual["prefab"] as JObject;
                var actualRoot =
                    actualPrefab?["instanceRoot"]
                        is JValue { Type: JTokenType.Boolean } ar &&
                    (bool)ar.Value!;
                var actualPath =
                    actualPrefab?["assetPath"]
                        is JValue { Type: JTokenType.String } ap
                        ? (string?)ap.Value
                        : null;
                if (i == 0 || completely)
                {
                    // The target root — or every entry under "completely" —
                    // must stand outside any Prefab instance.
                    if (actualRoot || (actualPath?.Length ?? 0) != 0)
                    {
                        reason = i == 0
                            ? "the target is still part of a Prefab instance"
                            : "a nested Prefab instance remains in the hierarchy";
                        return false;
                    }
                }
                else if (string.Equals(
                    recordedPath, targetAssetPath, StringComparison.Ordinal))
                {
                    // A recorded member of the target instance is freed by
                    // the outermost unpack — it must be instance-free too.
                    if (actualRoot || (actualPath?.Length ?? 0) != 0)
                    {
                        reason =
                            "a member of the target instance is still part " +
                            "of a Prefab instance";
                        return false;
                    }
                }
                else if (!JToken.DeepEquals(
                    expected["prefab"], actual["prefab"]))
                {
                    reason =
                        "a nested Prefab instance's state differs from the snapshot";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Whether a recorded or freshly re-read structure list is fully
        /// inspectable — every entry carries the <c>state</c> member
        /// <see cref="StructureEntry"/> writes and a <c>components</c>
        /// list whose members are all complete
        /// <see cref="ComponentEvidence.Entry"/> records. A structure
        /// carrying an unverifiable marker can never prove two
        /// hierarchies identical, not even against an identical marker
        /// (fail closed, Issue #313).
        /// </summary>
        internal static bool StructureVerifiable(JToken? structure)
        {
            if (structure is not JArray entries)
            {
                return false;
            }
            foreach (var entry in entries)
            {
                if (entry is not JObject obj ||
                    obj["state"] is null ||
                    !ComponentsVerifiable(obj["components"]))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Whether a recorded or freshly re-read override list is fully
        /// inspectable — the <see cref="JArray"/> <see cref="OverridesOf"/>
        /// returns on success, with no unverifiable marker anywhere inside
        /// it. A "<unreadable:…>" list or a member's
        /// "<unsupported:…>" data can never prove the override lists
        /// identical (fail closed, Issue #313).
        /// </summary>
        internal static bool OverridesVerifiable(JToken? overrides) =>
            overrides is JArray && !ContainsUnverifiableMarker(overrides);

        // Every entry must be a complete ComponentEvidence.Entry — a
        // missing script's "<missing>" marker or an entry without its
        // normalized "content" is unverifiable evidence, never proof the
        // composition is unchanged (Issue #313).
        private static bool ComponentsVerifiable(JToken? components)
        {
            if (components is not JArray entries)
            {
                return false;
            }
            foreach (var entry in entries)
            {
                if (!ComponentEvidence.IsVerifiableEntry(entry))
                {
                    return false;
                }
            }
            return true;
        }

        // A marker string leaf anywhere in the document means the member
        // was never inspected: the "<missing>" component marker, and the
        // "<unreadable:…>" / "<unsupported:…>" strings OverridesOf and
        // NormalizeMember record. Marker-bearing evidence can never prove
        // sameness (Issue #313).
        private static bool ContainsUnverifiableMarker(JToken token)
        {
            if (token is JValue { Type: JTokenType.String } value)
            {
                return value.Value is string text &&
                    (text == ComponentEvidence.MissingComponentMarker ||
                     text.StartsWith(UnreadablePrefix, StringComparison.Ordinal) ||
                     text.StartsWith(UnsupportedPrefix, StringComparison.Ordinal));
            }
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties())
                {
                    if (ContainsUnverifiableMarker(property.Value))
                    {
                        return true;
                    }
                }
            }
            else if (token is JArray array)
            {
                foreach (var element in array)
                {
                    if (ContainsUnverifiableMarker(element))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // The ordered component entries of one object — the shared
        // ComponentEvidence list, which records a missing script's slot
        // as its unverifiable marker so the comparisons above treat it as
        // never identical (fail closed, Issue #313).
        private static JArray ComponentsOf(GameObject go) =>
            ComponentEvidence.ComponentsOf(go);

        private static void Walk(Transform current, JArray entries)
        {
            entries.Add(StructureEntry(current.gameObject));
            for (var i = 0; i < current.childCount; i++)
            {
                Walk(current.GetChild(i), entries);
            }
        }

        // One m_Modification member normalized generically: arrays recurse,
        // object references resolve to their persistent guid · localFileId
        // key or the session handle of a scene object, scalars carry their
        // value and generic structs recurse member by member. An
        // uninspectable member type records a marker — and identical
        // markers can never prove the member unchanged, because the
        // comparisons refuse equality on marker-bearing evidence (fail
        // closed, Issue #313).
        private static JToken NormalizeMember(SerializedProperty property)
        {
            if (property.isArray &&
                property.propertyType == SerializedPropertyType.Generic)
            {
                var array = new JArray();
                for (var i = 0; i < property.arraySize; i++)
                {
                    array.Add(NormalizeMember(property.GetArrayElementAtIndex(i)));
                }
                return array;
            }
            switch (property.propertyType)
            {
                case SerializedPropertyType.ObjectReference:
                    return ReferenceKey(property.objectReferenceValue);
                case SerializedPropertyType.ExposedReference:
                    return ReferenceKey(property.exposedReferenceValue);
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.Enum:
                case SerializedPropertyType.LayerMask:
                    return new JValue(property.longValue);
                case SerializedPropertyType.Boolean:
                    return new JValue(property.boolValue);
                case SerializedPropertyType.String:
                    return new JValue(property.stringValue);
                case SerializedPropertyType.Float:
                    return new JValue(property.floatValue);
                case SerializedPropertyType.Generic:
                    var obj = new JObject();
                    var child = property.Copy();
                    var end = child.GetEndProperty();
                    var depth = property.depth;
                    if (child.Next(enterChildren: true))
                    {
                        while (child.depth > depth &&
                            !SerializedProperty.EqualContents(child, end))
                        {
                            obj[child.name] = NormalizeMember(child);
                            if (!child.Next(enterChildren: false))
                            {
                                break;
                            }
                        }
                    }
                    return obj;
                default:
                    return JValue.CreateString(
                        UnsupportedPrefix + property.propertyType + ">");
            }
        }

        // The identity key of an object reference inside the override
        // list: persistent objects name their asset guid and local file
        // id; scene objects carry their session handle. The reference is
        // evidence, never a resolvable output.
        private static JToken ReferenceKey(UnityEngine.Object? reference)
        {
            if (reference is null)
            {
                return JValue.CreateNull();
            }
            if (EditorUtility.IsPersistent(reference) &&
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    reference, out var guid, out var fileId))
            {
                return new JObject
                {
                    ["guid"] = guid,
                    ["localFileId"] = fileId,
                };
            }
            return new JObject
            {
                ["handle"] = SessionHandle.Write(reference),
            };
        }
    }
}
