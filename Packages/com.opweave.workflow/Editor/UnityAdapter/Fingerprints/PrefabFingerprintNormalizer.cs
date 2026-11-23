#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The SPEC §G6.1 / §0.6 SG-12 normalizer: a read-only traversal of a
    /// to-be-saved or saved Prefab content tree producing the structured
    /// Canonical-JSON document whose SHA-256 is the PrefabContentFingerprint
    /// v1. Anything the walk cannot normalize fails closed into
    /// <see cref="PrefabFingerprintResult.Unknown"/> — never a cheaper hash
    /// (SPEC §G6.1 禁止).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The document carries the four SG-12 members:
    /// <c>hierarchy</c> — one record per GameObject in child-index-path
    /// order with its serialized GameObject fields;
    /// <c>components</c> — one record per Component in (owner path,
    /// component order) with its type identity and every serialized
    /// property; <c>overrides</c> — the property modifications and
    /// added/removed Components/GameObjects of every nested Prefab instance
    /// and of a Variant root, sorted by target path Ordinal; and
    /// <c>references</c> — the deduplicated set of external references,
    /// each <c>{guid, localFileId}</c>.
    /// </para>
    /// <para>
    /// Identification follows SPEC §G6.1: objects inside the content are
    /// identified by their index path from the root plus component order;
    /// references outside it use guid + local file ID. Values that cannot
    /// be matched across a save boundary — Unity-internal bookkeeping
    /// fields (<c>m_ObjectHideFlags</c>, <c>m_CorrespondingSourceObject</c>,
    /// <c>m_PrefabInstance</c>, <c>m_PrefabAsset</c>,
    /// <c>serializedVersion</c>), renumbered IDs, InstanceIDs, addresses —
    /// never enter the hash.
    /// </para>
    /// <para>
    /// <b>Local, reversible choices where SPEC leaves the shape open.</b>
    /// The four member names and ordering rules are SG-12; the inner record
    /// shapes are chosen here and pinned by the committed golden bytes.
    /// Unity version compatibility of a comparison is enforced at the
    /// snapshot level (<see cref="PrefabContentFingerprint.Check"/>), not
    /// inside the document. Property types the v1 walker does not support
    /// produce <see cref="PrefabFingerprintResult.Unknown"/>, as do
    /// Missing Scripts, dangling references, unconnected or missing-asset
    /// Prefab instances, non-finite floats and JSON values outside the
    /// Canonical JSON model.
    /// </para>
    /// </remarks>
    internal static class PrefabFingerprintNormalizer
    {
        /// <summary>
        /// Builds the normalized document for the content rooted at
        /// <paramref name="contentRoot"/> (a Prefab contents editing root,
        /// a saved Prefab asset's root, or any GameObject tree intended to
        /// become a Prefab asset). Read-only: nothing is written back.
        /// </summary>
        internal static PrefabFingerprintResult Build(GameObject contentRoot) =>
            Build(contentRoot, persistedRootName: null);

        /// <summary>
        /// As <see cref="Build(GameObject)"/>, but the root GameObject's
        /// name is normalized to <paramref name="persistedRootName"/> —
        /// the name the destination asset file gives the saved root
        /// (SPEC §G6.1: the pre-save fingerprint must describe the
        /// content Unity will persist).
        /// </summary>
        internal static PrefabFingerprintResult Build(
            GameObject contentRoot, string? persistedRootName)
        {
            try
            {
                var walker = new Walker(persistedRootName);
                return walker.Run(contentRoot);
            }
            catch (Exception e)
            {
                // Read-only traversal must never throw across the API
                // boundary; any Unity-side failure is an unnormalizable
                // state and answers Unknown (SPEC §G6.1).
                return new PrefabFingerprintResult.Unknown(
                    "the content could not be serialized for fingerprinting: " + e.GetType().Name);
            }
        }

        private sealed class Walker
        {
            // The file name the saved root carries — applied to the root
            // GameObject's name and m_Name so a pre-save document matches
            // the post-save one (SaveAsPrefabAsset renames the root).
            private readonly string? _persistedRootName;

            internal Walker(string? persistedRootName) =>
                _persistedRootName = persistedRootName;

            private readonly Dictionary<UnityEngine.Object, ObjectIdentity> _identity =
                new Dictionary<UnityEngine.Object, ObjectIdentity>(ReferenceComparer.Instance);

            private readonly List<KeyValuePair<GameObject, int[]>> _nodes =
                new List<KeyValuePair<GameObject, int[]>>();

            private readonly List<JObject> _hierarchy = new List<JObject>();
            private readonly List<JObject> _components = new List<JObject>();
            private readonly List<JObject> _overrides = new List<JObject>();

            private readonly Dictionary<string, ExternalReference> _references =
                new Dictionary<string, ExternalReference>(StringComparer.Ordinal);

            private string? _unknown;

            internal PrefabFingerprintResult Run(GameObject root)
            {
                // A content root that is itself a Prefab instance root is
                // Variant content — a saved Variant loads as an instance
                // of its base, and a to-be-saved Variant is the same
                // instance. EmitNode normalizes it like a nested
                // instance (source reference plus the persisted
                // override set); only the sub-cases the walk cannot
                // prove — unconnected or missing-source instances,
                // dangling references — fail closed into Unknown
                // (SPEC §G6.1 実機検証).
                IndexHierarchy(root, Array.Empty<int>());
                if (_unknown is not null)
                {
                    return new PrefabFingerprintResult.Unknown(_unknown);
                }

                foreach (var pair in _nodes)
                {
                    EmitNode(pair.Key, pair.Value);
                    if (_unknown is not null)
                    {
                        return new PrefabFingerprintResult.Unknown(_unknown);
                    }
                }

                var document = new JObject
                {
                    ["prefabFingerprintVersion"] = PrefabContentFingerprint.CurrentVersion,
                    ["hierarchy"] = new JArray(_hierarchy),
                    ["components"] = new JArray(_components),
                    ["overrides"] = new JArray(SortedOverrides()),
                    ["references"] = new JArray(SortedReferences()),
                };
                JsonValue json;
                try
                {
                    json = JsonValue.From(document);
                }
                catch (Exception e) when (e is ArgumentException)
                {
                    return new PrefabFingerprintResult.Unknown(
                        "the normalized content is outside the JSON value model: " + e.Message);
                }
                return new PrefabFingerprintResult.Computed(
                    new PrefabFingerprint(PrefabContentFingerprint.CurrentVersion, json));
            }

            private bool Failed => _unknown is not null;

            private void Fail(string reason) => _unknown ??= reason;

            // ── Pass 1: object identity ─────────────────────────────────

            private void IndexHierarchy(GameObject node, int[] path)
            {
                _identity[node] = ObjectIdentity.ForGameObject(path);
                _nodes.Add(new KeyValuePair<GameObject, int[]>(node, path));

                var components = node.GetComponents<Component>();
                for (var i = 0; i < components.Length; i++)
                {
                    var component = components[i];
                    if (component == null)
                    {
                        Fail($"a missing script sits at component index {i} of {Describe(path)}");
                        continue;
                    }
                    _identity[component] = ObjectIdentity.ForComponent(path, i);
                }

                var transform = node.transform;
                for (var i = 0; i < transform.childCount; i++)
                {
                    var child = transform.GetChild(i).gameObject;
                    var childPath = new int[path.Length + 1];
                    Array.Copy(path, childPath, path.Length);
                    childPath[path.Length] = i;
                    IndexHierarchy(child, childPath);
                }
            }

            // ── Pass 2: hierarchy + components + prefab instances ───────

            private void EmitNode(GameObject node, int[] path)
            {
                var properties = SerializeProperties(node);
                var persistedName = path.Length == 0 ? _persistedRootName : null;
                if (persistedName is not null)
                {
                    properties["m_Name"] = new JValue(persistedName);
                }
                var nodeJson = new JObject
                {
                    ["name"] = persistedName ?? node.name,
                    ["path"] = PathToken(path),
                    ["properties"] = properties,
                };
                if (PrefabUtility.IsAnyPrefabInstanceRoot(node))
                {
                    var instance = InstanceInfo(node, path);
                    if (instance is not null)
                    {
                        nodeJson["prefab"] = instance;
                    }
                }
                _hierarchy.Add(nodeJson);

                var components = node.GetComponents<Component>();
                for (var i = 0; i < components.Length; i++)
                {
                    var component = components[i];
                    if (component == null)
                    {
                        continue; // already reported as a missing script in pass 1
                    }
                    var type = component.GetType();
                    _components.Add(new JObject
                    {
                        ["assembly"] = type.Assembly.GetName().Name,
                        ["index"] = i,
                        ["owner"] = PathToken(path),
                        ["properties"] = SerializeProperties(component),
                        ["type"] = type.FullName,
                    });
                    if (CanCarryManagedReferences(component) &&
                        SerializationUtility.HasManagedReferencesWithMissingTypes(component))
                    {
                        Fail($"a managed reference type is missing on component {i} of {Describe(path)}");
                        return;
                    }
                }
            }

            // HasManagedReferencesWithMissingTypes only supports targets
            // that can carry [SerializeReference] data and logs an error
            // for anything else (e.g. Transform), so it is called for
            // managed-reference-capable targets only.
            private static bool CanCarryManagedReferences(UnityEngine.Object target) =>
                target is MonoBehaviour || target is ScriptableObject;

            private JObject? InstanceInfo(GameObject node, int[] path)
            {
                var status = PrefabUtility.GetPrefabInstanceStatus(node);
                if (status != PrefabInstanceStatus.Connected)
                {
                    Fail($"the prefab instance at {Describe(path)} is not connected ({status})");
                    return null;
                }
                var sourcePath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(node);
                if (string.IsNullOrEmpty(sourcePath))
                {
                    Fail($"the prefab instance at {Describe(path)} has no source prefab asset");
                    return null;
                }
                var sourceRoot = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
                if (sourceRoot == null ||
                    !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                        sourceRoot, out var sourceGuid, out var sourceFileId))
                {
                    Fail($"the source prefab of the instance at {Describe(path)} cannot be identified");
                    return null;
                }
                RegisterExternal(sourceGuid, sourceFileId);
                CollectOverrides(node, path);
                return new JObject
                {
                    ["assetType"] = PrefabUtility.GetPrefabAssetType(node).ToString(),
                    ["source"] = new JObject
                    {
                        ["guid"] = sourceGuid,
                        ["localFileId"] = sourceFileId,
                    },
                };
            }

            private void CollectOverrides(GameObject instanceRoot, int[] instancePath)
            {
                // Variant root-name rule: the saved Variant root takes
                // the destination file name, so the m_Name override the
                // Variant records on the root's source GameObject is
                // normalized to that file name — mirroring the root
                // GameObject's own m_Name normalization.
                UnityEngine.Object? rootSource = null;
                if (instancePath.Length == 0 && _persistedRootName is not null)
                {
                    rootSource = PrefabUtility.GetCorrespondingObjectFromSource(instanceRoot);
                }
                var modifications = PrefabUtility.GetPropertyModifications(instanceRoot);
                if (modifications is not null)
                {
                    foreach (var mod in modifications)
                    {
                        // m_RootOrder is scene-side root ordering that
                        // Unity has not serialized since 2022.2; it can
                        // appear in the pre-save modification set but is
                        // never persisted, so it does not enter the
                        // document (Override-golden evidence).
                        if (mod.propertyPath == "m_RootOrder")
                        {
                            continue;
                        }
                        if (mod.target == null)
                        {
                            Fail($"an override of the instance at {Describe(instancePath)} has no target");
                            continue;
                        }
                        var value = mod.value is null
                            ? JValue.CreateNull()
                            : new JValue(mod.value);
                        if (rootSource is not null &&
                            mod.propertyPath == "m_Name" &&
                            ReferenceEquals(mod.target, rootSource))
                        {
                            value = new JValue(_persistedRootName!);
                        }
                        _overrides.Add(new JObject
                        {
                            ["instance"] = PathToken(instancePath),
                            ["kind"] = "property",
                            ["objectReference"] = mod.objectReference == null
                                ? JValue.CreateNull()
                                : NormalizeReference(mod.objectReference,
                                    $"override '{mod.propertyPath}' of {Describe(instancePath)}"),
                            ["propertyPath"] = mod.propertyPath ?? string.Empty,
                            ["target"] = NormalizeReference(mod.target,
                                $"override '{mod.propertyPath}' of {Describe(instancePath)}"),
                            ["value"] = value,
                        });
                    }
                }
                foreach (var added in PrefabUtility.GetAddedComponents(instanceRoot))
                {
                    _overrides.Add(new JObject
                    {
                        ["instance"] = PathToken(instancePath),
                        ["kind"] = "addedComponent",
                        ["target"] = NormalizeReference(
                            added.instanceComponent,
                            $"added component of {Describe(instancePath)}"),
                    });
                }
                foreach (var removed in PrefabUtility.GetRemovedComponents(instanceRoot))
                {
                    _overrides.Add(new JObject
                    {
                        ["instance"] = PathToken(instancePath),
                        ["kind"] = "removedComponent",
                        ["target"] = NormalizeReference(
                            removed.assetComponent,
                            $"removed component of {Describe(instancePath)}"),
                    });
                }
                foreach (var added in PrefabUtility.GetAddedGameObjects(instanceRoot))
                {
                    _overrides.Add(new JObject
                    {
                        ["instance"] = PathToken(instancePath),
                        ["kind"] = "addedGameObject",
                        ["target"] = NormalizeReference(
                            added.instanceGameObject,
                            $"added GameObject of {Describe(instancePath)}"),
                    });
                }
                foreach (var removed in PrefabUtility.GetRemovedGameObjects(instanceRoot))
                {
                    _overrides.Add(new JObject
                    {
                        ["instance"] = PathToken(instancePath),
                        ["kind"] = "removedGameObject",
                        ["target"] = NormalizeReference(
                            removed.assetGameObject,
                            $"removed GameObject of {Describe(instancePath)}"),
                    });
                }
            }

            // ── SerializedObject → normalized JSON ─────────────────────

            // Unity-internal bookkeeping fields whose values are relative
            // to the containing file or session — excluded per SPEC §G6.1
            // (renumbered IDs, InstanceIDs and addresses are never hashed).
            private static bool ExcludedProperty(string name) =>
                name == "m_ObjectHideFlags" ||
                name == "m_CorrespondingSourceObject" ||
                name == "m_PrefabInstance" ||
                name == "m_PrefabAsset" ||
                name == "serializedVersion";

            private JObject SerializeProperties(UnityEngine.Object target)
            {
                var properties = new JObject();
                // Managed-reference identity restarts per serialized
                // object: see ManagedIndexOf.
                _managedRefs.Clear();
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
                    if (ExcludedProperty(iterator.name))
                    {
                        continue;
                    }
                    properties[iterator.name] = NormalizeProperty(iterator);
                }
                while (iterator.Next(enterChildren: false));
                return properties;
            }

            private JToken NormalizeProperty(SerializedProperty property)
            {
                if (Failed)
                {
                    return JValue.CreateNull();
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
                        return Vec4Token(c.r, c.g, c.b, c.a, "r", "g", "b", "a", property.propertyPath);
                    }
                    case SerializedPropertyType.Vector2:
                    {
                        var v = property.vector2Value;
                        return VecToken(v.x, v.y, property.propertyPath);
                    }
                    case SerializedPropertyType.Vector3:
                    {
                        var v = property.vector3Value;
                        return VecToken(v.x, v.y, v.z, property.propertyPath);
                    }
                    case SerializedPropertyType.Vector4:
                    {
                        var v = property.vector4Value;
                        return VecToken(v.x, v.y, v.z, v.w, property.propertyPath);
                    }
                    case SerializedPropertyType.Quaternion:
                    {
                        var q = property.quaternionValue;
                        return Vec4Token(q.x, q.y, q.z, q.w, "x", "y", "z", "w", property.propertyPath);
                    }
                    case SerializedPropertyType.Vector2Int:
                    {
                        var v = property.vector2IntValue;
                        return new JObject { ["x"] = v.x, ["y"] = v.y };
                    }
                    case SerializedPropertyType.Vector3Int:
                    {
                        var v = property.vector3IntValue;
                        return new JObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };
                    }
                    case SerializedPropertyType.Rect:
                    {
                        var r = property.rectValue;
                        return Vec4Token(r.x, r.y, r.width, r.height,
                            "x", "y", "width", "height", property.propertyPath);
                    }
                    case SerializedPropertyType.RectInt:
                    {
                        var r = property.rectIntValue;
                        return new JObject
                        {
                            ["height"] = r.height, ["width"] = r.width,
                            ["x"] = r.x, ["y"] = r.y,
                        };
                    }
                    case SerializedPropertyType.Bounds:
                    {
                        var b = property.boundsValue;
                        return new JObject
                        {
                            ["center"] = VecToken(b.center.x, b.center.y, b.center.z,
                                property.propertyPath),
                            ["extents"] = VecToken(b.extents.x, b.extents.y, b.extents.z,
                                property.propertyPath),
                        };
                    }
                    case SerializedPropertyType.BoundsInt:
                    {
                        var b = property.boundsIntValue;
                        return new JObject
                        {
                            ["position"] = new JObject
                            {
                                ["x"] = b.position.x, ["y"] = b.position.y, ["z"] = b.position.z,
                            },
                            ["size"] = new JObject
                            {
                                ["x"] = b.size.x, ["y"] = b.size.y, ["z"] = b.size.z,
                            },
                        };
                    }
                    case SerializedPropertyType.Hash128:
                        return new JValue(property.hash128Value.ToString());
                    case SerializedPropertyType.AnimationCurve:
                        return CurveToken(property.animationCurveValue, property.propertyPath);
                    case SerializedPropertyType.Gradient:
                        return GradientToken(property.gradientValue, property.propertyPath);
                    case SerializedPropertyType.ObjectReference:
                    case SerializedPropertyType.ExposedReference:
                        return NormalizeObjectReference(property);
                    case SerializedPropertyType.ManagedReference:
                        return NormalizeManagedReference(property);
                    case SerializedPropertyType.Generic:
                        if (property.isArray)
                        {
                            var elements = new JArray();
                            for (var i = 0; i < property.arraySize && !Failed; i++)
                            {
                                elements.Add(NormalizeProperty(property.GetArrayElementAtIndex(i)));
                            }
                            return elements;
                        }
                        if (property.isFixedBuffer)
                        {
                            var elements = new JArray();
                            for (var i = 0; i < property.fixedBufferSize && !Failed; i++)
                            {
                                elements.Add(NormalizeProperty(property.GetFixedBufferElementAtIndex(i)));
                            }
                            return elements;
                        }
                        return GenericChildren(property);
                    default:
                        return Unknown(
                            $"unsupported serialized property type {property.propertyType} at '{property.propertyPath}'");
                }
            }

            // A child walk must stop exactly where parent's own children
            // end. On Unity 6000 the GetEndProperty sentinel does not
            // bound a serialized array element: for an element property
            // it lands after the array's last child, so an element's
            // walk absorbs its following siblings (each surfacing as a
            // bogus "data" member — GameObject.m_Component entries are
            // ComponentPair {component}, never "data"). The depth bound
            // is the reliable stop: a direct child always sits one level
            // below its parent and Next(enterChildren: false) never
            // descends, so leaving that level ends the walk. The end
            // sentinel stays as a second stop.
            private JObject GenericChildren(SerializedProperty parent)
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
                    members[iterator.name] = NormalizeProperty(iterator);
                    if (!iterator.Next(enterChildren: false))
                    {
                        break;
                    }
                }
                return members;
            }

            private JToken NormalizeManagedReference(SerializedProperty property)
            {
                object? managed;
                try
                {
                    managed = property.managedReferenceValue;
                }
                catch (Exception e) when (e is InvalidOperationException || e is ArgumentException)
                {
                    return Unknown($"managed reference '{property.propertyPath}' cannot be resolved");
                }
                var typename = property.managedReferenceFullTypename;
                if (managed is null)
                {
                    return string.IsNullOrEmpty(typename)
                        ? (JToken)JValue.CreateNull()
                        : Unknown(
                            $"managed reference '{property.propertyPath}' has an unresolvable type '{typename}'");
                }
                if (string.IsNullOrEmpty(typename))
                {
                    return Unknown(
                        $"managed reference '{property.propertyPath}' has no resolvable type");
                }
                // Assign the traversal index before descending so that a
                // managed object's index precedes those of the managed
                // references nested inside it.
                var index = ManagedIndexOf(managed);
                return new JObject
                {
                    ["fields"] = GenericChildren(property),
                    ["ref"] = index,
                    ["type"] = typename,
                };
            }

            // SPEC §G6.1: managedReferenceId values are renumbered at
            // save, so they must not enter the hash. A managed reference
            // is instead identified by the index of its first encounter
            // in the owning serialized object's traversal — two
            // properties referencing the same managed object share the
            // index, preserving the shared-reference relationship across
            // a save/reload boundary. The index restarts per serialized
            // object (SerializeProperties resets the table).
            private readonly Dictionary<object, int> _managedRefs =
                new Dictionary<object, int>(ManagedObjectComparer.Instance);

            private int ManagedIndexOf(object managed)
            {
                if (_managedRefs.TryGetValue(managed, out var index))
                {
                    return index;
                }
                index = _managedRefs.Count;
                _managedRefs.Add(managed, index);
                return index;
            }

            private JToken NormalizeObjectReference(SerializedProperty property)
            {
                UnityEngine.Object? target = property.propertyType == SerializedPropertyType.ExposedReference
                    ? property.exposedReferenceValue
                    : property.objectReferenceValue;
                if (target == null)
                {
                    return HasReferenceId(property)
                        ? Unknown($"unresolvable object reference at '{property.propertyPath}'")
                        : (JToken)JValue.CreateNull();
                }
                return NormalizeReference(target, $"'{property.propertyPath}'");
            }

            private JToken NormalizeReference(UnityEngine.Object target, string context)
            {
                if (target == null)
                {
                    return JValue.CreateNull();
                }
                if (_identity.TryGetValue(target, out var identity))
                {
                    var inner = new JObject { ["path"] = PathToken(identity.Path) };
                    if (identity.Component is int index)
                    {
                        inner["component"] = index;
                    }
                    return new JObject { ["internal"] = inner };
                }
                if (EditorUtility.IsPersistent(target) &&
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                        target, out var guid, out var fileId))
                {
                    RegisterExternal(guid, fileId);
                    return new JObject
                    {
                        ["external"] = new JObject
                        {
                            ["guid"] = guid,
                            ["localFileId"] = fileId,
                        },
                    };
                }
                return Unknown($"object reference at {context} cannot be identified");
            }

            // A null objectReferenceValue with a nonzero stored identifier
            // is a dangling reference — SPEC §G6.1 makes the whole
            // fingerprint Unknown, never silently null.
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

            // ── Leaf value tokens ───────────────────────────────────────

            private JToken DoubleToken(double value, string propertyPath) =>
                double.IsNaN(value) || double.IsInfinity(value)
                    ? Unknown($"non-finite number at '{propertyPath}'")
                    : (JToken)new JValue(value);

            private JToken VecToken(float x, float y, string propertyPath) => new JObject
            {
                ["x"] = DoubleToken(x, propertyPath),
                ["y"] = DoubleToken(y, propertyPath),
            };

            private JToken VecToken(float x, float y, float z, string propertyPath) => new JObject
            {
                ["x"] = DoubleToken(x, propertyPath),
                ["y"] = DoubleToken(y, propertyPath),
                ["z"] = DoubleToken(z, propertyPath),
            };

            private JToken VecToken(float x, float y, float z, float w, string propertyPath) => new JObject
            {
                ["w"] = DoubleToken(w, propertyPath),
                ["x"] = DoubleToken(x, propertyPath),
                ["y"] = DoubleToken(y, propertyPath),
                ["z"] = DoubleToken(z, propertyPath),
            };

            private JToken Vec4Token(
                float a, float b, float c, float d,
                string keyA, string keyB, string keyC, string keyD,
                string propertyPath) => new JObject
            {
                [keyA] = DoubleToken(a, propertyPath),
                [keyB] = DoubleToken(b, propertyPath),
                [keyC] = DoubleToken(c, propertyPath),
                [keyD] = DoubleToken(d, propertyPath),
            };

            private JToken CurveToken(AnimationCurve curve, string propertyPath)
            {
                var keys = new JArray();
                foreach (var key in curve.keys)
                {
                    keys.Add(new JObject
                    {
                        ["inTangent"] = DoubleToken(key.inTangent, propertyPath),
                        ["inWeight"] = DoubleToken(key.inWeight, propertyPath),
                        ["outTangent"] = DoubleToken(key.outTangent, propertyPath),
                        ["outWeight"] = DoubleToken(key.outWeight, propertyPath),
                        ["time"] = DoubleToken(key.time, propertyPath),
                        ["value"] = DoubleToken(key.value, propertyPath),
                        ["weightedMode"] = (int)key.weightedMode,
                    });
                }
                return new JObject
                {
                    ["keys"] = keys,
                    ["postWrapMode"] = (int)curve.postWrapMode,
                    ["preWrapMode"] = (int)curve.preWrapMode,
                };
            }

            private JToken GradientToken(Gradient gradient, string propertyPath)
            {
                var colorKeys = new JArray();
                foreach (var key in gradient.colorKeys)
                {
                    colorKeys.Add(new JObject
                    {
                        ["color"] = new JObject
                        {
                            ["a"] = DoubleToken(key.color.a, propertyPath),
                            ["b"] = DoubleToken(key.color.b, propertyPath),
                            ["g"] = DoubleToken(key.color.g, propertyPath),
                            ["r"] = DoubleToken(key.color.r, propertyPath),
                        },
                        ["time"] = DoubleToken(key.time, propertyPath),
                    });
                }
                var alphaKeys = new JArray();
                foreach (var key in gradient.alphaKeys)
                {
                    alphaKeys.Add(new JObject
                    {
                        ["alpha"] = DoubleToken(key.alpha, propertyPath),
                        ["time"] = DoubleToken(key.time, propertyPath),
                    });
                }
                return new JObject
                {
                    ["alphaKeys"] = alphaKeys,
                    ["colorKeys"] = colorKeys,
                    ["mode"] = (int)gradient.mode,
                };
            }

            // ── Helpers ────────────────────────────────────────────────

            private JToken Unknown(string reason)
            {
                Fail(reason);
                return JValue.CreateNull();
            }

            private void RegisterExternal(string guid, long fileId)
            {
                _references[guid + "" + fileId.ToString(CultureInfo.InvariantCulture)] =
                    new ExternalReference(guid, fileId);
            }

            private IEnumerable<JToken> SortedReferences()
            {
                var sorted = new List<ExternalReference>(_references.Values);
                sorted.Sort((x, y) =>
                {
                    var byGuid = string.CompareOrdinal(x.Guid, y.Guid);
                    return byGuid != 0 ? byGuid : x.FileId.CompareTo(y.FileId);
                });
                foreach (var reference in sorted)
                {
                    yield return new JObject
                    {
                        ["guid"] = reference.Guid,
                        ["localFileId"] = reference.FileId,
                    };
                }
            }

            private IEnumerable<JToken> SortedOverrides()
            {
                var sorted = new List<JObject>(_overrides);
                sorted.Sort((x, y) =>
                {
                    var byTarget = string.CompareOrdinal(
                        CanonicalText(x["target"]), CanonicalText(y["target"]));
                    if (byTarget != 0)
                    {
                        return byTarget;
                    }
                    var byKind = string.CompareOrdinal(
                        x["kind"]?.ToString(), y["kind"]?.ToString());
                    if (byKind != 0)
                    {
                        return byKind;
                    }
                    return string.CompareOrdinal(
                        x["propertyPath"]?.ToString(), y["propertyPath"]?.ToString());
                });
                return sorted;
            }

            private static string CanonicalText(JToken? token) =>
                token is null ? string.Empty : JsonValue.From(token).CanonicalText;

            private static JArray PathToken(int[] path)
            {
                var array = new JArray();
                foreach (var index in path)
                {
                    array.Add(new JValue(index));
                }
                return array;
            }

            private static string Describe(int[] path) =>
                "hierarchy path [" + string.Join(",", path) + "]";
        }

        private readonly struct ObjectIdentity
        {
            internal ObjectIdentity(int[] path, int? component)
            {
                Path = path;
                Component = component;
            }

            internal static ObjectIdentity ForGameObject(int[] path) =>
                new ObjectIdentity(path, null);

            internal static ObjectIdentity ForComponent(int[] path, int index) =>
                new ObjectIdentity(path, index);

            internal int[] Path { get; }

            internal int? Component { get; }
        }

        private readonly struct ExternalReference
        {
            internal ExternalReference(string guid, long fileId)
            {
                Guid = guid;
                FileId = fileId;
            }

            internal string Guid { get; }

            internal long FileId { get; }
        }

        private sealed class ReferenceComparer : IEqualityComparer<UnityEngine.Object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();

            public bool Equals(UnityEngine.Object? x, UnityEngine.Object? y) =>
                ReferenceEquals(x, y);

            public int GetHashCode(UnityEngine.Object obj) =>
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        private sealed class ManagedObjectComparer : IEqualityComparer<object>
        {
            internal static readonly ManagedObjectComparer Instance = new ManagedObjectComparer();

            public new bool Equals(object? x, object? y) =>
                ReferenceEquals(x, y);

            public int GetHashCode(object obj) =>
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
