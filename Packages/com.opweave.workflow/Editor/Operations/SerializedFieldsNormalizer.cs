#nullable enable

using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace OpWeave.Operations
{
    /// <summary>
    /// The single-object serialized-content normalizer behind
    /// <c>unity.asset.create_scriptable_object</c>'s §F5 evidence: a
    /// read-only traversal of one asset's <see cref="SerializedObject"/>
    /// producing the structured document Reconcile compares member by
    /// member. "Serialized content matches the expectation" means the
    /// normalized documents are deep-equal — never a looser notion like
    /// name, path or save time (SPEC §F5 common rule 4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The leaf shapes mirror <c>PrefabFingerprintNormalizer</c> (SPEC §G6.1,
    /// §0.6 SG-12) — integer family to Int64, float/double to a finite JSON
    /// number, vectors and colors to component objects, object references to
    /// <c>{guid, localFileId}</c> — because a serialized ScriptableObject is
    /// the same kind of document a prefab's component records carry. The
    /// same bookkeeping fields are excluded
    /// (<c>m_ObjectHideFlags</c>, <c>m_CorrespondingSourceObject</c>,
    /// <c>m_PrefabInstance</c>, <c>m_PrefabAsset</c>,
    /// <c>serializedVersion</c>): values that are relative to the containing
    /// file or session cannot match across a save boundary.
    /// </para>
    /// <para>
    /// <b>Fail closed.</b> Anything the walk cannot normalize — an
    /// unsupported property type, a non-finite number, a dangling or
    /// non-persistent object reference, a non-null managed reference, a
    /// nested structure deeper than <see cref="MaxDepth"/> — reports a
    /// failure instead of degrading to a weaker notion of content. The
    /// Operation then simply carries no expected content, and Reconcile's
    /// verdict is Unknown (SPEC §F5 common rule 6), never a Completed built
    /// on partial evidence.
    /// </para>
    /// </remarks>
    internal static class SerializedFieldsNormalizer
    {
        /// <summary>
        /// The deepest member nesting the walk accepts. A managed reference
        /// is the only structure that could recurse unboundedly — it is
        /// refused outright — so the bound exists only against pathological
        /// generic layouts, well past anything a real ScriptableObject
        /// serializes.
        /// </summary>
        internal const int MaxDepth = 16;

        /// <summary>
        /// Normalizes the serialized content of <paramref name="target"/>
        /// into <paramref name="content"/>. Returns
        /// <see langword="false"/> — with <paramref name="failure"/>
        /// describing the reason — when the content cannot be normalized;
        /// <paramref name="content"/> is then <see langword="null"/>.
        /// </summary>
        internal static bool TryNormalize(
            UnityEngine.Object target, out JObject? content, out string? failure) =>
            TryNormalize(target, null, out content, out failure);

        /// <summary>
        /// As <see cref="TryNormalize(UnityEngine.Object, out JObject?, out string?)"/>,
        /// but <paramref name="exclude"/> may skip an individual top-level
        /// property — for the caller-owned exclusions (a Component's
        /// scene-hierarchy links) that must not hide a same-named user field.
        /// </summary>
        internal static bool TryNormalize(
            UnityEngine.Object target,
            Func<SerializedProperty, bool>? exclude,
            out JObject? content, out string? failure)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }
            var walker = new Walker();
            var properties = walker.Serialize(target, exclude);
            if (walker.Failure is not null)
            {
                content = null;
                failure = walker.Failure;
                return false;
            }
            content = properties;
            failure = null;
            return true;
        }

        private sealed class Walker
        {
            internal string? Failure { get; private set; }

            private void Fail(string reason) => Failure ??= reason;

            private bool Failed => Failure is not null;

            // The Unity-internal bookkeeping fields excluded from the
            // comparison (SPEC §G6.1 / SG-12): file- and session-relative
            // values that can never match across a save boundary.
            private static bool ExcludedProperty(string name) =>
                name == "m_ObjectHideFlags" ||
                name == "m_CorrespondingSourceObject" ||
                name == "m_PrefabInstance" ||
                name == "m_PrefabAsset" ||
                name == "serializedVersion";

            internal JObject Serialize(
                UnityEngine.Object target, Func<SerializedProperty, bool>? exclude)
            {
                var properties = new JObject();
                SerializedObject serialized;
                try
                {
                    serialized = new SerializedObject(target);
                }
                catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
                {
                    Fail($"the serialized data of {target.GetType().Name} cannot be opened");
                    return properties;
                }

                var iterator = serialized.GetIterator();
                if (!iterator.Next(enterChildren: true))
                {
                    return properties;
                }
                do
                {
                    if (Failed)
                    {
                        break;
                    }
                    if (ExcludedProperty(iterator.name) ||
                        (exclude is not null && exclude(iterator)))
                    {
                        continue;
                    }
                    properties[iterator.name] = Normalize(iterator, 0);
                }
                while (iterator.Next(enterChildren: false));
                return properties;
            }

            private JToken Normalize(SerializedProperty property, int depth)
            {
                if (Failed)
                {
                    return JValue.CreateNull();
                }
                if (depth > MaxDepth)
                {
                    return Unknown(
                        $"nested serialized content deeper than {MaxDepth} at '{property.propertyPath}'");
                }
                switch (property.propertyType)
                {
                    case SerializedPropertyType.Integer:
                    case SerializedPropertyType.Enum:
                    case SerializedPropertyType.LayerMask:
                    case SerializedPropertyType.Character:
                    case SerializedPropertyType.RenderingLayerMask:
                        switch (property.numericType)
                        {
                            case SerializedPropertyNumericType.UInt64:
                            {
                                var wide = property.ulongValue;
                                if (wide > long.MaxValue)
                                {
                                    return Unknown(
                                        $"integer '{property.propertyPath}' is outside the Int64 range");
                                }
                                return new JValue((long)wide);
                            }
                            case SerializedPropertyNumericType.UInt8:
                            case SerializedPropertyNumericType.UInt16:
                            case SerializedPropertyNumericType.UInt32:
                                return new JValue((long)property.ulongValue);
                            default:
                                return new JValue(property.longValue);
                        }
                    case SerializedPropertyType.Boolean:
                        return new JValue(property.boolValue);
                    case SerializedPropertyType.Float:
                        return property.numericType == SerializedPropertyNumericType.Double
                            ? DoubleToken(property.doubleValue, property.propertyPath)
                            : DoubleToken(property.floatValue, property.propertyPath);
                    case SerializedPropertyType.String:
                        return new JValue(property.stringValue);
                    case SerializedPropertyType.Color:
                    {
                        var c = property.colorValue;
                        return new JObject
                        {
                            ["r"] = DoubleToken(c.r, property.propertyPath),
                            ["g"] = DoubleToken(c.g, property.propertyPath),
                            ["b"] = DoubleToken(c.b, property.propertyPath),
                            ["a"] = DoubleToken(c.a, property.propertyPath),
                        };
                    }
                    case SerializedPropertyType.Vector2:
                    {
                        var v = property.vector2Value;
                        return new JObject
                        {
                            ["x"] = DoubleToken(v.x, property.propertyPath),
                            ["y"] = DoubleToken(v.y, property.propertyPath),
                        };
                    }
                    case SerializedPropertyType.Vector3:
                    {
                        var v = property.vector3Value;
                        return new JObject
                        {
                            ["x"] = DoubleToken(v.x, property.propertyPath),
                            ["y"] = DoubleToken(v.y, property.propertyPath),
                            ["z"] = DoubleToken(v.z, property.propertyPath),
                        };
                    }
                    case SerializedPropertyType.Vector4:
                    {
                        var v = property.vector4Value;
                        return new JObject
                        {
                            ["x"] = DoubleToken(v.x, property.propertyPath),
                            ["y"] = DoubleToken(v.y, property.propertyPath),
                            ["z"] = DoubleToken(v.z, property.propertyPath),
                            ["w"] = DoubleToken(v.w, property.propertyPath),
                        };
                    }
                    case SerializedPropertyType.Quaternion:
                    {
                        var q = property.quaternionValue;
                        return new JObject
                        {
                            ["x"] = DoubleToken(q.x, property.propertyPath),
                            ["y"] = DoubleToken(q.y, property.propertyPath),
                            ["z"] = DoubleToken(q.z, property.propertyPath),
                            ["w"] = DoubleToken(q.w, property.propertyPath),
                        };
                    }
                    case SerializedPropertyType.Vector2Int:
                    {
                        var v = property.vector2IntValue;
                        return new JObject { ["x"] = v.x, ["y"] = v.y };
                    }
                    case SerializedPropertyType.Vector3Int:
                    {
                        var v = property.vector3IntValue;
                        return new JObject
                        {
                            ["x"] = v.x, ["y"] = v.y, ["z"] = v.z,
                        };
                    }
                    case SerializedPropertyType.Rect:
                    {
                        var r = property.rectValue;
                        return new JObject
                        {
                            ["x"] = DoubleToken(r.x, property.propertyPath),
                            ["y"] = DoubleToken(r.y, property.propertyPath),
                            ["width"] = DoubleToken(r.width, property.propertyPath),
                            ["height"] = DoubleToken(r.height, property.propertyPath),
                        };
                    }
                    case SerializedPropertyType.RectInt:
                    {
                        var r = property.rectIntValue;
                        return new JObject
                        {
                            ["x"] = r.x, ["y"] = r.y,
                            ["width"] = r.width, ["height"] = r.height,
                        };
                    }
                    case SerializedPropertyType.Bounds:
                    {
                        var b = property.boundsValue;
                        return new JObject
                        {
                            ["center"] = new JObject
                            {
                                ["x"] = DoubleToken(b.center.x, property.propertyPath),
                                ["y"] = DoubleToken(b.center.y, property.propertyPath),
                                ["z"] = DoubleToken(b.center.z, property.propertyPath),
                            },
                            ["extents"] = new JObject
                            {
                                ["x"] = DoubleToken(b.extents.x, property.propertyPath),
                                ["y"] = DoubleToken(b.extents.y, property.propertyPath),
                                ["z"] = DoubleToken(b.extents.z, property.propertyPath),
                            },
                        };
                    }
                    case SerializedPropertyType.BoundsInt:
                    {
                        var b = property.boundsIntValue;
                        return new JObject
                        {
                            ["position"] = new JObject
                            {
                                ["x"] = b.position.x,
                                ["y"] = b.position.y,
                                ["z"] = b.position.z,
                            },
                            ["size"] = new JObject
                            {
                                ["x"] = b.size.x, ["y"] = b.size.y, ["z"] = b.size.z,
                            },
                        };
                    }
                    case SerializedPropertyType.Hash128:
                        return new JValue(property.hash128Value.ToString());
                    case SerializedPropertyType.ObjectReference:
                    case SerializedPropertyType.ExposedReference:
                        return NormalizeReference(property);
                    case SerializedPropertyType.ManagedReference:
                    {
                        // A null managed reference is stable content; a
                        // live one is refused — traversal-order identity
                        // across a save boundary is the Prefab fingerprint's
                        // own machinery, and a half measure would claim
                        // content equality it cannot prove (SPEC §F5).
                        object? managed;
                        try
                        {
                            managed = property.managedReferenceValue;
                        }
                        catch (Exception e) when (
                            e is InvalidOperationException || e is ArgumentException)
                        {
                            return Unknown(
                                $"managed reference '{property.propertyPath}' cannot be resolved");
                        }
                        if (managed is null)
                        {
                            return string.IsNullOrEmpty(property.managedReferenceFullTypename)
                                ? (JToken)JValue.CreateNull()
                                : Unknown(
                                    $"managed reference '{property.propertyPath}' has an unresolvable type");
                        }
                        return Unknown(
                            $"managed reference '{property.propertyPath}' carries a live object");
                    }
                    case SerializedPropertyType.Generic:
                        if (property.isArray)
                        {
                            var elements = new JArray();
                            for (var i = 0; i < property.arraySize && !Failed; i++)
                            {
                                elements.Add(Normalize(property.GetArrayElementAtIndex(i), depth + 1));
                            }
                            return elements;
                        }
                        if (property.isFixedBuffer)
                        {
                            var elements = new JArray();
                            for (var i = 0; i < property.fixedBufferSize && !Failed; i++)
                            {
                                elements.Add(Normalize(property.GetFixedBufferElementAtIndex(i), depth + 1));
                            }
                            return elements;
                        }
                        return GenericChildren(property, depth);
                    default:
                        return Unknown(
                            $"unsupported serialized property type {property.propertyType} at '{property.propertyPath}'");
                }
            }

            // A child walk stops where the parent's own children end — the
            // PrefabFingerprintNormalizer's depth bound, because on Unity
            // 6000 the GetEndProperty sentinel does not bound a serialized
            // array element. The end sentinel stays as a second stop.
            private JObject GenericChildren(SerializedProperty parent, int depth)
            {
                var members = new JObject();
                var parentDepth = parent.depth;
                var iterator = parent.Copy();
                var end = iterator.GetEndProperty();
                if (!iterator.Next(enterChildren: true))
                {
                    return members;
                }
                while (!Failed &&
                       iterator.depth > parentDepth &&
                       !SerializedProperty.EqualContents(iterator, end))
                {
                    members[iterator.name] = Normalize(iterator, depth + 1);
                    if (!iterator.Next(enterChildren: false))
                    {
                        break;
                    }
                }
                return members;
            }

            private JToken NormalizeReference(SerializedProperty property)
            {
                UnityEngine.Object? target =
                    property.propertyType == SerializedPropertyType.ExposedReference
                        ? property.exposedReferenceValue
                        : property.objectReferenceValue;
                if (target == null)
                {
                    return HasReferenceId(property)
                        ? Unknown($"unresolvable object reference at '{property.propertyPath}'")
                        : (JToken)JValue.CreateNull();
                }
                if (EditorUtility.IsPersistent(target) &&
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                        target, out var guid, out var fileId))
                {
                    return new JObject
                    {
                        ["guid"] = guid,
                        ["localFileId"] = fileId,
                    };
                }
                return Unknown(
                    $"object reference at '{property.propertyPath}' cannot be identified");
            }

            // A null objectReferenceValue with a nonzero stored identifier
            // is a dangling reference — it is not content, it is damage the
            // expectation cannot name (SPEC §G6.1's own reading).
            private static bool HasReferenceId(SerializedProperty property)
            {
#if UNITY_6000_4_OR_NEWER
                return !property.objectReferenceEntityIdValue.Equals(EntityId.None);
#else
#pragma warning disable CS0618 // InstanceID APIs are the 6000.0–6000.3 generation (L-04).
                return property.objectReferenceInstanceIDValue != 0;
#pragma warning restore CS0618
#endif
            }

            private JToken DoubleToken(double value, string propertyPath) =>
                double.IsNaN(value) || double.IsInfinity(value)
                    ? Unknown($"non-finite number at '{propertyPath}'")
                    : (JToken)new JValue(value);

            private JToken Unknown(string reason)
            {
                Fail(reason);
                return JValue.CreateNull();
            }
        }
    }
}
