#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;

namespace OpWeave.Operations
{
    /// <summary>
    /// The shared §F5 evidence helpers of the Component Operations: the
    /// snapshot shapes the Reconcilable <c>unity.component.add</c> and
    /// <c>unity.component.remove</c> write at Preflight and read back at
    /// Reconcile — the owning GameObject's GameObjectRef, the ordered
    /// component entries (type class-id · session handle · normalized
    /// serialized content) and the Editor session id of SPEC §F5 common
    /// rule 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The snapshot JSON is the implementation detail §F5 leaves to this
    /// ticket; the evidence it carries is the normative §F5 table row. The
    /// session handle survives a domain reload but not an Editor restart —
    /// which is exactly what the "same session" condition needs.
    /// </para>
    /// </remarks>
    internal static class ComponentEvidence
    {
        /// <summary>The marker recorded in place of a full
        /// <see cref="Entry"/> for a dead component slot — a missing
        /// script. It is evidence of shape, not of inspected content:
        /// unverifiable, so two identical markers can never prove the
        /// components identical (fail closed, Issue #313).</summary>
        internal const string MissingComponentMarker = "<missing>";

        /// <summary>One ordered component entry: the class-id of the
        /// component's exact type, its session handle and its normalized
        /// serialized content — the "型・順序・handle" evidence plus the
        /// "他の Component が変わらず" proof both §F5 rows need. A
        /// component whose content cannot be normalized is recorded
        /// without a <c>content</c> member, which later comparisons treat
        /// as unverifiable rather than unchanged (fail closed).</summary>
        internal static JObject Entry(Component component)
        {
            var entry = new JObject
            {
                ["type"] = UnityClassRelationProvider.ClassIdOf(component.GetType()),
                ["handle"] = SessionHandle.Write(component),
            };
            if (TryNormalizeComponent(component, out var content, out _))
            {
                entry["content"] = content;
            }
            return entry;
        }

        /// <summary>
        /// Whether a recorded component entry is fully inspectable —
        /// the exact-type class-id, a readable session handle and the
        /// normalized <c>content</c> of a complete <see cref="Entry"/>.
        /// The <see cref="MissingComponentMarker"/> entry and an entry
        /// whose content could not be normalized are both unverifiable:
        /// two identical markers still prove nothing, so a comparison
        /// must treat them as unequal (fail closed, Issue #313).
        /// </summary>
        internal static bool IsVerifiableEntry(JToken? entry) =>
            entry is JObject obj &&
            obj["type"] is JValue { Type: JTokenType.String } &&
            HandleOf(obj) is not null &&
            obj["content"] is JObject;

        /// <summary>
        /// Normalizes a component's serialized content the way
        /// <see cref="SerializedFieldsNormalizer"/> does, except the
        /// Unity-owned scene-hierarchy links — <c>m_GameObject</c> on every
        /// Component and <c>m_Father</c>/<c>m_Children</c> on a Transform —
        /// which name non-persistent scene objects the reference normalizer
        /// can never identify. The exclusion is decided by the property's
        /// identity, never its name alone: a user-defined serialized field
        /// that happens to share one of these names stays in the document
        /// and keeps being compared.
        /// </summary>
        internal static bool TryNormalizeComponent(
            Component component, out JObject? content, out string? failure) =>
            SerializedFieldsNormalizer.TryNormalize(
                component, p => IsUnityLinkage(p, component), out content, out failure);

        // Unity's own hierarchy links are identifiable by what they point at:
        // m_GameObject is the owner's GameObject reference, m_Father is the
        // Transform's parent reference and m_Children is its child array. A
        // same-named user field is never skipped — it either carries a
        // different type or a value these links cannot hold.
        private static bool IsUnityLinkage(SerializedProperty property, Component component)
        {
            if (property.name == "m_GameObject")
            {
                return property.propertyType == SerializedPropertyType.ObjectReference &&
                    property.objectReferenceValue == component.gameObject;
            }
            if (component is Transform transform)
            {
                if (property.name == "m_Father")
                {
                    return property.propertyType == SerializedPropertyType.ObjectReference &&
                        property.objectReferenceValue == transform.parent;
                }
                if (property.name == "m_Children")
                {
                    return property.isArray &&
                        property.arraySize == transform.childCount;
                }
            }
            return false;
        }

        /// <summary>The ordered component entries of a GameObject, in
        /// <c>GetComponents</c> order — the "所有 GameObject の Component の
        /// 一覧" both rows record. A dead (missing script) slot records
        /// the <see cref="MissingComponentMarker"/> marker instead of a
        /// full <see cref="Entry"/> — unverifiable evidence that never
        /// proves the list unchanged (fail closed).</summary>
        internal static JArray ComponentsOf(GameObject owner)
        {
            var components = new JArray();
            foreach (var component in LiveComponents(owner))
            {
                components.Add(component == null
                    ? new JObject { ["type"] = MissingComponentMarker }
                    : Entry(component));
            }
            return components;
        }

        /// <summary>The live components of a GameObject, in the same
        /// <c>GetComponents</c> order the snapshot entries follow.</summary>
        internal static Component[] LiveComponents(GameObject owner) =>
            owner.GetComponents<Component>();

        /// <summary>The session handle key of a snapshot component entry, or
        /// <see langword="null"/> when the member is not a readable handle.</summary>
        internal static ulong? HandleOf(JToken? entry)
        {
            if (entry is not JObject obj || obj["handle"] is not { } token)
            {
                return null;
            }
            return SessionHandle.TryRead(token, out var raw) ? raw : (ulong?)null;
        }

        /// <summary>
        /// Whether <paramref name="live"/> and the snapshot entry name the
        /// same unchanged component: identical session handle, identical
        /// exact-type class-id and identical normalized serialized
        /// content. A property edit keeps handle and type intact, so the
        /// content half is what proves the "他の Component が変わらず"
        /// condition of §F5; an entry or a live object whose content
        /// cannot be normalized is unverifiable, never unchanged.
        /// </summary>
        internal static bool SameComponent(JToken? snapshotEntry, Component live)
        {
            // An uninspectable entry — a "<missing>" marker or one
            // without normalized content — and a dead live slot are
            // unverifiable, never "the same component" (fail closed,
            // Issue #313).
            if (live == null || !IsVerifiableEntry(snapshotEntry) ||
                HandleOf(snapshotEntry) is not { } raw)
            {
                return false;
            }
            var entry = (JObject)snapshotEntry!;
            if (SessionHandle.RawOf(live) != raw)
            {
                return false;
            }
            var recordedType = entry["type"] is JValue { Type: JTokenType.String } type
                ? (string?)type.Value
                : null;
            if (!string.Equals(
                recordedType,
                UnityClassRelationProvider.ClassIdOf(live.GetType()),
                StringComparison.Ordinal))
            {
                return false;
            }
            return entry["content"] is JObject recordedContent &&
                TryNormalizeComponent(live, out var liveContent, out _) &&
                JToken.DeepEquals(recordedContent, liveContent);
        }

        /// <summary>
        /// Whether the live components equal the recorded list element for
        /// element, in order — the "list unchanged" half of NotApplied and
        /// the "list minus the target / list plus the additions" halves of
        /// Completed (SPEC §F5).
        /// </summary>
        internal static bool SameComponentList(
            JToken? snapshotComponents, IReadOnlyList<Component> live)
        {
            if (snapshotComponents is not JArray recorded || recorded.Count != live.Count)
            {
                return false;
            }
            for (var i = 0; i < live.Count; i++)
            {
                if (!SameComponent(recorded[i], live[i]))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Whether the first <paramref name="prefixCount"/> live components
        /// equal the recorded list element for element, in order — the
        /// "他の Component が変わらず" prefix of the component.add row's
        /// Completed verdict.
        /// </summary>
        internal static bool SameComponentPrefix(
            JToken? snapshotComponents, IReadOnlyList<Component> live, int prefixCount)
        {
            if (snapshotComponents is not JArray recorded ||
                recorded.Count != prefixCount || live.Count < prefixCount)
            {
                return false;
            }
            for (var i = 0; i < prefixCount; i++)
            {
                if (!SameComponent(recorded[i], live[i]))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>The class-id recorded for a snapshot component or
        /// expected-addition entry, or <see langword="null"/>.</summary>
        internal static string? TypeOf(JToken? entry) =>
            entry is JObject obj && obj["type"] is JValue { Type: JTokenType.String } type
                ? (string?)type.Value
                : null;

        /// <summary>The fail-closed verdict of both Operations (SPEC §F5).</summary>
        internal static ReconcileResult.Unknown Unknown(string reason) =>
            new ReconcileResult.Unknown(reason);
    }
}
