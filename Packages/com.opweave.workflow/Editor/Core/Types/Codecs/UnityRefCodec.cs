#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core
{
    /// <summary>
    /// The JSON-shape Codec of the unity.* Ref types of SPEC §B5: a JSON object
    /// whose <c>kind</c> discriminator names the actual Ref type of the value,
    /// followed by the persistent keys (guid, path, globalId) and the session
    /// key (<c>handle</c>) of that kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The SPEC does not spell the <c>kind</c> strings out beyond
    /// <c>"gameObject"</c>; the codec fixes the remaining discriminators as the
    /// camelCase role names (<c>scene</c>, <c>prefabInstance</c>,
    /// <c>component</c>, <c>asset</c>, <c>prefabAsset</c>), exposed as the
    /// <c>UnityShapeTypes.*Kind</c> constants so the UnityAdapter emits the
    /// same spellings (SG-10). This is a local, reversible choice.
    /// </para>
    /// <para>
    /// A value validates for an expected Ref type when its <c>kind</c> names
    /// the expected type or a type declared below it in SPEC §B5's base chain:
    /// <c>unity.GameObjectRef</c> accepts <c>gameObject</c> and
    /// <c>prefabInstance</c>, <c>unity.AssetRef&lt;T&gt;</c> accepts
    /// <c>asset</c> and <c>prefabAsset</c>, and <c>unity.ObjectRef</c>
    /// accepts every known kind. Matching a concrete <c>T</c> against the
    /// value's actual class belongs to resolution and assignability (§B6, §G2)
    /// — the Codec on the open generic definition never sees <c>T</c>.
    /// </para>
    /// <para>
    /// Per SPEC §B5, WF parameters may carry only a subset of a kind's keys
    /// (path alone is valid), so every key except <c>kind</c> is optional;
    /// keys outside the kind's declared set are rejected, matching the
    /// strict-shape rule SPEC applies to InputSource objects. Key shapes where
    /// the SPEC leaves them open (local, reversible choices): <c>path</c> is a
    /// HierarchyPath inside scene refs and an AssetPath inside scene/asset
    /// refs — the table annotates the former only; <c>scene</c> is a nested
    /// SceneRef object or a scene-name string; <c>guid</c>,
    /// <c>prefabAsset</c> and <c>globalId</c> are strings; <c>handle</c> is an
    /// Int64 integer or a decimal-digit string (6000.5+ EntityId is an
    /// unsigned 64-bit value that can exceed Int64, so the digit-string
    /// spelling of L-04 is also accepted and normalized to an integer when it
    /// fits); <c>type</c> is a class-id string; <c>index</c> is a non-negative
    /// integer; <c>localId</c> is an integer.
    /// </para>
    /// <para>
    /// <see cref="Normalize"/> is the identity except for digit-string
    /// <c>handle</c> keys, which convert to the equivalent integer token when
    /// the digits fit Int64 — the single persisted form for both spellings of
    /// the same handle (SPEC §B7.1). Conversion recurses through the nested
    /// <c>scene</c> and <c>owner</c> Ref objects.
    /// </para>
    /// </remarks>
    internal sealed class UnityRefCodec : ITypeSchemaCodec
    {
        internal static readonly UnityRefCodec ObjectRef = new UnityRefCodec(
            "unity.ObjectRef",
            UnityShapeTypes.SceneKind,
            UnityShapeTypes.GameObjectKind,
            UnityShapeTypes.PrefabInstanceKind,
            UnityShapeTypes.ComponentKind,
            UnityShapeTypes.AssetKind,
            UnityShapeTypes.PrefabAssetKind);

        internal static readonly UnityRefCodec SceneRef = new UnityRefCodec(
            "unity.SceneRef", UnityShapeTypes.SceneKind);

        internal static readonly UnityRefCodec GameObjectRef = new UnityRefCodec(
            "unity.GameObjectRef", UnityShapeTypes.GameObjectKind, UnityShapeTypes.PrefabInstanceKind);

        internal static readonly UnityRefCodec PrefabInstanceRef = new UnityRefCodec(
            "unity.PrefabInstanceRef", UnityShapeTypes.PrefabInstanceKind);

        internal static readonly UnityRefCodec ComponentRef = new UnityRefCodec(
            "unity.ComponentRef<T>", UnityShapeTypes.ComponentKind);

        internal static readonly UnityRefCodec AssetRef = new UnityRefCodec(
            "unity.AssetRef<T>", UnityShapeTypes.AssetKind, UnityShapeTypes.PrefabAssetKind);

        internal static readonly UnityRefCodec PrefabAssetRef = new UnityRefCodec(
            "unity.PrefabAssetRef", UnityShapeTypes.PrefabAssetKind);

        // kind → the Ref Type ID it discriminates (SPEC §B5).
        private static readonly IReadOnlyDictionary<string, string> s_kindTypeIds =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { UnityShapeTypes.SceneKind, "unity.SceneRef" },
                { UnityShapeTypes.GameObjectKind, "unity.GameObjectRef" },
                { UnityShapeTypes.PrefabInstanceKind, "unity.PrefabInstanceRef" },
                { UnityShapeTypes.ComponentKind, "unity.ComponentRef<T>" },
                { UnityShapeTypes.AssetKind, "unity.AssetRef<T>" },
                { UnityShapeTypes.PrefabAssetKind, "unity.PrefabAssetRef" },
            };

        // kind → the JSON keys the kind's Ref may carry (SPEC §B5 table).
        private static readonly IReadOnlyDictionary<string, HashSet<string>> s_keySets =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
            {
                { UnityShapeTypes.SceneKind, Set("kind", "path", "guid") },
                { UnityShapeTypes.GameObjectKind, Set("kind", "scene", "path", "globalId", "handle") },
                { UnityShapeTypes.PrefabInstanceKind, Set("kind", "scene", "path", "globalId", "handle", "prefabAsset") },
                { UnityShapeTypes.ComponentKind, Set("kind", "owner", "type", "index") },
                { UnityShapeTypes.AssetKind, Set("kind", "guid", "path", "localId") },
                { UnityShapeTypes.PrefabAssetKind, Set("kind", "guid", "path") },
            };

        private static readonly HashSet<string> s_sceneOnly =
            new HashSet<string>(new[] { UnityShapeTypes.SceneKind }, StringComparer.Ordinal);

        private static readonly HashSet<string> s_gameObjectOnly =
            new HashSet<string>(
                new[] { UnityShapeTypes.GameObjectKind, UnityShapeTypes.PrefabInstanceKind },
                StringComparer.Ordinal);

        private readonly string _typeId;
        private readonly HashSet<string> _kinds;

        private UnityRefCodec(string typeId, params string[] kinds)
        {
            _typeId = typeId;
            _kinds = new HashSet<string>(kinds, StringComparer.Ordinal);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// <para>
        /// The fragment is the union of the accepted kinds' key sets with
        /// <c>kind</c> as the required discriminator enum — the closest
        /// §B8-subset expression of a kind-conditional shape (no
        /// <c>oneOf</c>/<c>if</c>), so a key accepted by one kind may pass
        /// the schema on another; the Codec stays the authority.
        /// </para>
        /// <para>
        /// Members: <c>path</c> is the asset-path or hierarchy-path pattern
        /// (the alternation of both when the accepted kinds mix);
        /// <c>scene</c> is the scene-reference object or a free string;
        /// <c>owner</c> is exactly a GameObjectRef value and is emitted as
        /// <c>$ref</c> into <c>$defs</c>; <c>handle</c> is an integer or a
        /// digit string (L-04); <c>type</c> a class-id string;
        /// <c>index</c> a non-negative integer; the rest plain strings or
        /// integers per §B5.
        /// </para>
        /// </remarks>
        public void WriteSchema(JObject schema, TypeSchemaDefs defs)
        {
            var accepted = new JArray();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var assetPath = false;
            var hierarchyPath = false;
            for (var i = 0; i < s_kindOrder.Length; i++)
            {
                var kind = s_kindOrder[i];
                if (!_kinds.Contains(kind))
                {
                    continue;
                }
                accepted.Add(kind);
                keys.UnionWith(s_keySets[kind]);
                assetPath |= kind == UnityShapeTypes.SceneKind ||
                    kind == UnityShapeTypes.AssetKind ||
                    kind == UnityShapeTypes.PrefabAssetKind;
                hierarchyPath |= kind == UnityShapeTypes.GameObjectKind ||
                    kind == UnityShapeTypes.PrefabInstanceKind;
            }
            var properties = new JObject
            {
                ["kind"] = new JObject { ["type"] = "string", ["enum"] = accepted },
            };
            for (var i = 0; i < s_memberOrder.Length; i++)
            {
                var member = s_memberOrder[i];
                if (keys.Contains(member))
                {
                    properties[member] = MemberSchema(member, assetPath, hierarchyPath, defs);
                }
            }
            schema["type"] = "object";
            schema["properties"] = properties;
            schema["required"] = new JArray("kind");
            schema["additionalProperties"] = false;
        }

        // The discriminator order of SPEC §B5's kind table — the emission
        // order of every 'kind' enum, so output is deterministic.
        private static readonly string[] s_kindOrder =
        {
            UnityShapeTypes.SceneKind,
            UnityShapeTypes.GameObjectKind,
            UnityShapeTypes.PrefabInstanceKind,
            UnityShapeTypes.ComponentKind,
            UnityShapeTypes.AssetKind,
            UnityShapeTypes.PrefabAssetKind,
        };

        // Member emission order for the unioned key sets; 'kind' is always
        // written first, separately.
        private static readonly string[] s_memberOrder =
        {
            "path", "guid", "scene", "globalId", "handle",
            "prefabAsset", "owner", "type", "index", "localId",
        };

        private static JObject MemberSchema(
            string member, bool assetPath, bool hierarchyPath, TypeSchemaDefs defs)
        {
            switch (member)
            {
                case "path":
                    var pattern = assetPath && hierarchyPath
                        ? UnityShapePatterns.AssetPath.ToString() + "|" + HierarchyPathPattern
                        : assetPath
                            ? UnityShapePatterns.AssetPath.ToString()
                            : HierarchyPathPattern;
                    return new JObject { ["type"] = "string", ["pattern"] = pattern };
                case "scene":
                    // A nested SceneRef object or a free scene-name string
                    // (see ValidateMember): the object keywords apply only
                    // to objects, so strings pass unconstrained.
                    return new JObject
                    {
                        ["type"] = new JArray("object", "string"),
                        ["properties"] = new JObject
                        {
                            ["kind"] = new JObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JArray(UnityShapeTypes.SceneKind),
                            },
                            ["path"] = new JObject
                            {
                                ["type"] = "string",
                                ["pattern"] = UnityShapePatterns.AssetPath.ToString(),
                            },
                            ["guid"] = new JObject { ["type"] = "string" },
                        },
                        ["required"] = new JArray("kind"),
                        ["additionalProperties"] = false,
                    };
                case "owner":
                    // Exactly a GameObjectRef value (s_gameObjectOnly) — a
                    // genuine type reference, emitted into $defs (SPEC §B8).
                    return defs.RefFor("unity.GameObjectRef");
                case "handle":
                    return new JObject
                    {
                        ["type"] = new JArray("integer", "string"),
                        ["pattern"] = HandleDigitsPattern,
                    };
                case "type":
                    return new JObject { ["type"] = "string", ["pattern"] = ClassIdPattern };
                case "index":
                    return new JObject { ["type"] = "integer", ["minimum"] = 0 };
                case "localId":
                    return new JObject { ["type"] = "integer" };
                default:
                    // guid, globalId, prefabAsset.
                    return new JObject { ["type"] = "string" };
            }
        }

        private static readonly string HierarchyPathPattern =
            TypeSchemas.ToSchemaPattern(UnityShapePatterns.HierarchyPath.ToString());

        private static readonly string HandleDigitsPattern =
            TypeSchemas.ToSchemaPattern(UnityShapePatterns.HandleDigits.ToString());

        private static readonly string ClassIdPattern =
            TypeSchemas.ToSchemaPattern(UnityShapePatterns.ClassId.ToString());

        /// <inheritdoc/>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints)
        {
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            var diagnostics = new List<Diagnostic>();
            var token = value.ToToken();
            if (token.Type != JTokenType.Object)
            {
                diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_TYPE_003, "Value is not a Ref JSON object."));
            }
            else
            {
                ValidateRefObject((JObject)token, _kinds, _typeId, "$", diagnostics);
            }
            CodecConstraints.Check(token, constraints, diagnostics);
            return diagnostics;
        }

        /// <inheritdoc/>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        public JsonValue Normalize(JsonValue value)
        {
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            var token = value.ToToken();
            if (token.Type != JTokenType.Object)
            {
                return value;
            }
            var obj = (JObject)token;
            return NormalizeRefObject(obj) ? JsonValue.From(obj) : value;
        }

        // Validates one Ref object: the 'kind' discriminator against the
        // accepted set, then every present key against the actual kind's key
        // set and shape. 'path' is the JsonPath of the object itself.
        private static void ValidateRefObject(
            JObject obj,
            HashSet<string> allowedKinds,
            string expectedTypeId,
            string path,
            List<Diagnostic> sink)
        {
            var kindToken = obj["kind"];
            if (kindToken is null)
            {
                sink.Add(ErrorCodes.Create(
                    ErrorCodes.OW_TYPE_003,
                    "Ref object has no 'kind' discriminator.",
                    jsonPath: MemberPath(path, "kind")));
                return;
            }
            if (kindToken.Type != JTokenType.String)
            {
                sink.Add(ErrorCodes.Create(
                    ErrorCodes.OW_TYPE_003,
                    "Ref 'kind' is not a JSON string.",
                    jsonPath: MemberPath(path, "kind")));
                return;
            }
            var kind = (string)((JValue)kindToken).Value!;
            if (!s_kindTypeIds.ContainsKey(kind))
            {
                sink.Add(ErrorCodes.Create(
                    ErrorCodes.OW_TYPE_003,
                    $"Ref kind '{kind}' is not a known ref kind.",
                    jsonPath: MemberPath(path, "kind")));
                return;
            }
            if (!allowedKinds.Contains(kind))
            {
                sink.Add(ErrorCodes.Create(
                    ErrorCodes.OW_TYPE_003,
                    $"Ref kind '{kind}' does not identify a {expectedTypeId}.",
                    jsonPath: MemberPath(path, "kind")));
                return;
            }
            foreach (var property in obj.Properties())
            {
                if (property.Name == "kind")
                {
                    continue;
                }
                var memberPath = MemberPath(path, property.Name);
                if (!s_keySets[kind].Contains(property.Name))
                {
                    sink.Add(ErrorCodes.Create(
                        ErrorCodes.OW_TYPE_003,
                        $"Ref '{kind}' object has an unknown key '{property.Name}'.",
                        jsonPath: memberPath));
                    continue;
                }
                ValidateMember(kind, property, memberPath, sink);
            }
        }

        private static void ValidateMember(
            string kind, JProperty property, string memberPath, List<Diagnostic> sink)
        {
            var token = property.Value;
            switch (property.Name)
            {
                case "path":
                {
                    var isAsset = kind == UnityShapeTypes.SceneKind ||
                        kind == UnityShapeTypes.AssetKind ||
                        kind == UnityShapeTypes.PrefabAssetKind;
                    var pattern = isAsset
                        ? UnityShapePatterns.AssetPath
                        : UnityShapePatterns.HierarchyPath;
                    RequireText(token, pattern, memberPath, sink, isAsset
                        ? "Ref 'path' is not an asset path ('Assets/…' or 'Packages/…')."
                        : "Ref 'path' is not a hierarchy path ('/Root/Child').");
                    break;
                }
                case "guid":
                case "prefabAsset":
                case "globalId":
                    if (token.Type != JTokenType.String)
                    {
                        sink.Add(Fail(memberPath,
                            $"Ref '{property.Name}' is not a JSON string."));
                    }
                    break;
                case "scene":
                    if (token.Type == JTokenType.Object)
                    {
                        ValidateRefObject(
                            (JObject)token, s_sceneOnly, "unity.SceneRef", memberPath, sink);
                    }
                    else if (token.Type != JTokenType.String)
                    {
                        sink.Add(Fail(memberPath,
                            "Ref 'scene' is not a scene reference or a scene path."));
                    }
                    break;
                case "handle":
                    if (token.Type == JTokenType.Integer)
                    {
                        break;
                    }
                    if (token.Type != JTokenType.String ||
                        !UnityShapePatterns.HandleDigits.IsMatch((string)((JValue)token).Value!))
                    {
                        sink.Add(Fail(memberPath,
                            "Ref 'handle' is not an integer or a digit string."));
                    }
                    break;
                case "owner":
                    if (token.Type == JTokenType.Object)
                    {
                        ValidateRefObject(
                            (JObject)token, s_gameObjectOnly, "unity.GameObjectRef", memberPath, sink);
                    }
                    else
                    {
                        sink.Add(Fail(memberPath,
                            "Ref 'owner' is not a GameObject reference."));
                    }
                    break;
                case "type":
                    RequireText(token, UnityShapePatterns.ClassId, memberPath, sink,
                        "Ref 'type' is not a class identifier.");
                    break;
                case "index":
                    if (token.Type != JTokenType.Integer ||
                        (long)((JValue)token).Value! < 0)
                    {
                        sink.Add(Fail(memberPath,
                            "Ref 'index' is not a non-negative integer."));
                    }
                    break;
                case "localId":
                    if (token.Type != JTokenType.Integer)
                    {
                        sink.Add(Fail(memberPath,
                            "Ref 'localId' is not an integer."));
                    }
                    break;
            }
        }

        private static void RequireText(
            JToken token,
            System.Text.RegularExpressions.Regex pattern,
            string memberPath,
            List<Diagnostic> sink,
            string message)
        {
            if (token.Type != JTokenType.String ||
                !pattern.IsMatch((string)((JValue)token).Value!))
            {
                sink.Add(Fail(memberPath, message));
            }
        }

        // Rewrites digit-string 'handle' keys to integer tokens (when the value
        // fits Int64) inside this object and its nested 'scene'/'owner' Refs.
        private static bool NormalizeRefObject(JObject obj)
        {
            var changed = false;
            foreach (var property in obj.Properties())
            {
                switch (property.Name)
                {
                    case "handle":
                        if (property.Value.Type == JTokenType.String)
                        {
                            var text = (string)((JValue)property.Value).Value!;
                            if (UnityShapePatterns.HandleDigits.IsMatch(text) &&
                                long.TryParse(
                                    text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var handle))
                            {
                                property.Value = new JValue(handle);
                                changed = true;
                            }
                        }
                        break;
                    case "scene":
                    case "owner":
                        if (property.Value.Type == JTokenType.Object)
                        {
                            changed |= NormalizeRefObject((JObject)property.Value);
                        }
                        break;
                }
            }
            return changed;
        }

        private static HashSet<string> Set(params string[] keys) =>
            new HashSet<string>(keys, StringComparer.Ordinal);

        private static string MemberPath(string path, string name) =>
            path == "$" ? "$." + name : path + "." + name;

        private static Diagnostic Fail(string memberPath, string message) =>
            ErrorCodes.Create(ErrorCodes.OW_TYPE_003, message, jsonPath: memberPath);
    }
}
