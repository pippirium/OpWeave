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
    /// <c>unity.asset.create_scriptable_object</c> (SPEC §I.3): creates a
    /// <see cref="ScriptableObject"/> asset of the named type at
    /// <c>path</c>, applying each entry of <c>values</c> to the type's
    /// serialized fields. An existing path is never overwritten.
    /// Immediate, Undo None, <see cref="Idempotency.Reconcilable"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Type and path.</b> <c>type</c> carries the §I.3
    /// <c>type_exists</c> requirement — checked statically against a
    /// constant and again at run time by the §G1 evaluator — and is
    /// resolved here against the domain's type space: a name no assembly
    /// knows, a type that is not a <see cref="ScriptableObject"/>, and a
    /// type that cannot be instantiated (abstract, a generic definition)
    /// are all OW-UNITY-006, the §J "type not found or cannot be added"
    /// code. <c>path</c> is a write path judged by
    /// <see cref="AssetPathPolicy"/> with the §G1 <c>path_writable</c>
    /// requirement; the §I.3 <c>AssetPath(.asset)</c> note is read
    /// literally — the path must carry the <c>.asset</c> extension — and
    /// its parent folder must already exist (OW-UNITY-005).
    /// </para>
    /// <para>
    /// <b>Values.</b> <c>values</c> maps a serialized field name (or a
    /// <c>SerializedObject</c> property path under it) to the JSON value to
    /// apply through <see cref="SerializedProperty"/> — the supported set
    /// of SPEC §G5 item 7 read onto an asset: Integer, Boolean, Float,
    /// String, Enum by member name, Color, Vector2/3/4, Quaternion,
    /// Vector2Int/3Int, ObjectReference (a §B5 Ref JSON, resolved through
    /// G2), arrays and Generic members to a depth of three. A name that
    /// resolves to no field, a value the property cannot take, and any
    /// unsupported property kind are OW-UNITY-007, the §J property-
    /// operation failure — nothing is written over an unapplyable input.
    /// </para>
    /// <para>
    /// <b>Reconciliation (§F5).</b> Preflight persists the §F5 evidence row
    /// — the normalized target path, whether anything occupied it, the type
    /// name, the declared values and the serialized content expected from a
    /// default instance with those values applied — computed on an
    /// in-memory probe instance that is never persisted (the evidence §F5
    /// asks for: "values から期待するシリアライズ内容") — plus the Editor
    /// session id (evidence only; a file artifact re-identifies across an
    /// Editor restart by its permanent path/guid keys, §F5 common rule 3).
    /// <see cref="Reconcile"/> decides only from that evidence: nothing at
    /// the path is <see cref="ReconcileResult.NotApplied"/>; an asset whose
    /// CLR type is exactly the recorded one and whose normalized serialized
    /// content is deep-equal to the expectation is
    /// <see cref="ReconcileResult.Completed"/> with the AssetRef output
    /// restored; everything else is <see cref="ReconcileResult.Unknown"/>.
    /// A type whose content cannot be normalized carries no expectation,
    /// and Reconcile answers Unknown rather than comparing less
    /// (§F5 common rule 6).
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class CreateScriptableObjectOperation : OperationBase
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.asset.create_scriptable_object";

        internal const string PathPort = "path";
        internal const string TypePort = "type";
        internal const string ValuesPort = "values";
        internal const string AssetPort = "asset";

        /// <summary>The §I.3 <c>AssetPath(.asset)</c> extension.</summary>
        internal const string FileExtension = ".asset";

        // The §G5 item 7 depth of nested member application.
        private const int MaxValuesDepth = 3;

        private static readonly JsonValue s_emptySnapshot =
            JsonValue.From(new JObject());

        /// <inheritdoc/>
        public override OperationDescriptor Descriptor { get; } = BuildDescriptor();

        /// <inheritdoc/>
        public override PreflightResult Preflight(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            // §D8 item 1: a pending port has no value to inspect in a Dry
            // Run, and a required port with no value yields no evidence to
            // record.
            if (ctx.IsPending(PathPort) || ctx.IsPending(TypePort) ||
                ctx.IsPending(ValuesPort) ||
                !ctx.IsPresent(PathPort) || !ctx.IsPresent(TypePort))
            {
                return base.Preflight(ctx);
            }

            var inputs = ReadInputs(ctx);
            if (!inputs.Ok)
            {
                return new PreflightResult(
                    inputs.Diagnostics, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }
            var plan = inputs.Value!;
            var diagnostic = CheckDestination(plan.Path);
            if (diagnostic is not null)
            {
                return new PreflightResult(
                    new[] { diagnostic }, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }

            // The §F5 expected serialized content: a default instance of the
            // type with the declared values applied — a probe that lives
            // only in memory and is destroyed before Preflight returns.
            var probe = ScriptableObject.CreateInstance(plan.Type);
            JObject? expected = null;
            try
            {
                probe.name = AssetArtifact.FileBaseName(plan.Path);
                var applyError = plan.Values is not null
                    ? ApplyValues(probe, plan.Values)
                    : null;
                if (applyError is not null)
                {
                    return new PreflightResult(
                        new[] { applyError },
                        Array.Empty<PlannedChange>(),
                        s_emptySnapshot);
                }
                if (!SerializedFieldsNormalizer.TryNormalize(
                        probe, out var content, out _))
                {
                    content = null;
                }
                expected = content;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(probe);
            }

            var snapshot = new JObject
            {
                ["path"] = plan.Path,
                ["pathExisted"] = false,
                ["type"] = plan.Type.FullName,
                ["editorSessionId"] = EditorSessionId.GetOrCreate(),
            };
            if (plan.Values is not null)
            {
                snapshot["values"] = plan.Values;
            }
            if (expected is not null)
            {
                snapshot["expectedContent"] = expected;
            }
            return new PreflightResult(
                Array.Empty<Diagnostic>(),
                Array.Empty<PlannedChange>(),
                JsonValue.From(snapshot));
        }

        /// <inheritdoc/>
        public override StepResult Execute(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            var inputs = ReadInputs(ctx);
            if (!inputs.Ok)
            {
                return StepResult.Failed(inputs.Diagnostics[0]);
            }
            var plan = inputs.Value!;
            var diagnostic = CheckDestination(plan.Path);
            if (diagnostic is not null)
            {
                return StepResult.Failed(diagnostic);
            }

            var instance = ScriptableObject.CreateInstance(plan.Type);
            // The importer names the main asset after its file; pinning
            // the name keeps the saved content byte-deterministic with
            // the Preflight expectation.
            instance.name = AssetArtifact.FileBaseName(plan.Path);
            var applyError = plan.Values is not null
                ? ApplyValues(instance, plan.Values)
                : null;
            if (applyError is not null)
            {
                UnityEngine.Object.DestroyImmediate(instance);
                return StepResult.Failed(applyError);
            }

            AssetDatabase.CreateAsset(instance, plan.Path);
            var saved = AssetDatabase.LoadAssetAtPath<ScriptableObject>(plan.Path);
            if (saved == null || saved.GetType() != plan.Type)
            {
                if (!EditorUtility.IsPersistent(instance))
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                }
                return StepResult.Failed(NotCreated());
            }
            var guid = AssetDatabase.AssetPathToGUID(plan.Path);

            ctx.Changes.Created(
                new ChangeTarget("asset", plan.Path, guid), undoable: false);
            ctx.SetOutput(AssetPort, saved);
            return StepResult.Succeeded();
        }

        /// <inheritdoc/>
        public override ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }
            if (snapshot.ToToken() is not JObject obj)
            {
                return Unknown("The Preflight snapshot is not an object.");
            }
            if (!Member(obj, "path", JTokenType.String, out var pathToken) ||
                !Member(obj, "pathExisted", JTokenType.Boolean, out var existedToken) ||
                !Member(obj, "type", JTokenType.String, out var typeToken) ||
                !Member(obj, "editorSessionId", JTokenType.String, out _))
            {
                return Unknown("The Preflight snapshot is missing a required evidence member.");
            }
            var values = obj["values"];
            if (values is not null && values.Type != JTokenType.Object)
            {
                return Unknown("The Preflight snapshot is missing a required evidence member.");
            }
            var expected = obj["expectedContent"] as JObject;
            var path = (string)pathToken!;
            var pathExisted = (bool)existedToken!;
            var typeName = (string)typeToken!;

            // §F5 rule 6: an expectation that cannot be re-established — a
            // type the domain no longer resolves — can never prove a verdict.
            var type = ResolveType(typeName);
            if (type is null || !typeof(ScriptableObject).IsAssignableFrom(type))
            {
                return Unknown("The snapshot's type can no longer be resolved.");
            }

            var physical = AssetArtifact.ToPhysicalPath(path);
            var guid = AssetDatabase.AssetPathToGUID(
                path, AssetPathToGUIDOptions.OnlyExistingAssets);
            var fileExists = physical is not null && File.Exists(physical);
            var dirExists = physical is not null && Directory.Exists(physical);
            var metaExists = physical is not null && File.Exists(physical + ".meta");
            if (!fileExists && !dirExists && !metaExists && guid.Length == 0)
            {
                // Nothing occupies the target: the step provably did not
                // apply and re-execution can only create, never duplicate.
                return new ReconcileResult.NotApplied();
            }
            if (pathExisted)
            {
                return Unknown(
                    "A pre-existing artifact occupies the target path; it cannot be attributed to the step.");
            }
            if (dirExists)
            {
                return Unknown("A directory occupies the target path.");
            }
            if (!fileExists)
            {
                return Unknown(metaExists
                    ? "A leftover .meta file without a file occupies the target path."
                    : "An asset database entry without a file occupies the target path.");
            }

            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (asset == null || guid.Length == 0)
            {
                // A file at the path that imports as another asset type, or
                // is not imported at all: the §F5 partial side effect.
                return Unknown("The artifact at the target path is not a ScriptableObject asset.");
            }
            if (asset.GetType() != type)
            {
                return Unknown("The asset's type does not match the recorded input exactly.");
            }
            if (expected is null)
            {
                return Unknown("The snapshot carries no expected serialized content to compare.");
            }
            if (!SerializedFieldsNormalizer.TryNormalize(
                    asset, out var actual, out var failure))
            {
                return Unknown("The artifact's serialized content cannot be normalized: " + failure);
            }
            if (!JToken.DeepEquals(expected, actual))
            {
                return Unknown("The asset's serialized content does not match the expected content.");
            }

            var outputs = new Dictionary<string, JsonValue>(StringComparer.Ordinal)
            {
                [AssetPort] = JsonValue.From(new JObject
                {
                    ["kind"] = "asset",
                    ["guid"] = guid,
                    ["path"] = path,
                }),
            };
            return new ReconcileResult.Completed(outputs);
        }

        // ── Inputs ───────────────────────────────────────────────────────

        private sealed class StepInputs
        {
            internal string Path = string.Empty;
            internal Type Type = typeof(ScriptableObject);
            internal JObject? Values;
        }

        // The validated inputs of a Preflight/Execute call — every port is
        // re-checked at step time because a value may arrive through a data
        // edge the static phases could not see (SPEC §D3, §E5).
        private static Result<StepInputs> ReadInputs(StepContext ctx)
        {
            var diagnostics = new List<Diagnostic>();

            var path = AssetPathPolicy.CheckWritePath(
                ctx.Get<string>(PathPort), PathPort);
            if (!path.Ok)
            {
                diagnostics.AddRange(path.Diagnostics);
            }
            else if (!path.Value!.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(InvalidExtension());
            }

            var typeName = ctx.Get<string>(TypePort) ?? string.Empty;
            var type = ResolveType(typeName);
            if (type is null || !typeof(ScriptableObject).IsAssignableFrom(type) ||
                type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
            {
                diagnostics.Add(TypeNotCreatable());
            }

            JObject? values = null;
            if (ctx.IsPresent(ValuesPort))
            {
                if (ctx.Get<JsonValue>(ValuesPort).ToToken() is JObject map)
                {
                    values = map;
                }
                else
                {
                    diagnostics.Add(InvalidValues());
                }
            }

            if (diagnostics.Count != 0)
            {
                return Result<StepInputs>.Failure(diagnostics);
            }
            return Result<StepInputs>.Success(new StepInputs
            {
                Path = path.Value!,
                Type = type!,
                Values = values,
            });
        }

        // The domain type-space resolution of a fully qualified type name —
        // the same answer the UnityTypeProbe gives the type_exists
        // requirement: Type.GetType first (mscorlib and assembly-qualified
        // spellings), then every loaded assembly by full name.
        private static Type? ResolveType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return null;
            }
            Type? direct = null;
            try
            {
                direct = Type.GetType(typeName, throwOnError: false);
            }
            catch (Exception)
            {
                // A malformed assembly-qualified name resolves to nothing.
            }
            if (direct is not null)
            {
                return direct;
            }
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? found = null;
                try
                {
                    found = assembly.GetType(typeName, throwOnError: false);
                }
                catch (Exception)
                {
                    // A dynamic or partially loaded assembly may fail a name
                    // lookup; it simply has no answer for this name.
                }
                if (found is not null)
                {
                    return found;
                }
            }
            return null;
        }

        // The destination checks of the create contract: the parent folder
        // must already exist and the path must be completely free — a file,
        // a folder, an orphan .meta or a known asset all count as occupied.
        private static Diagnostic? CheckDestination(string path)
        {
            var parent = AssetArtifact.ParentFolder(path);
            var physical = AssetArtifact.ToPhysicalPath(path);
            if (physical is null || parent is null || !AssetDatabase.IsValidFolder(parent))
            {
                return FolderMissing();
            }
            return AssetArtifact.Occupied(physical, path) ? TargetOccupied() : null;
        }

        // ── Values application ───────────────────────────────────────────

        // Applies every entry of the declared values map to the instance's
        // serialized fields; the first entry that cannot be applied is the
        // OW-UNITY-007 failure and nothing is committed.
        private static Diagnostic? ApplyValues(UnityEngine.Object target, JObject values)
        {
            var serialized = new SerializedObject(target);
            var refs = new RefResolver();
            foreach (var entry in values.Properties())
            {
                var property = serialized.FindProperty(entry.Name);
                if (property is null)
                {
                    return FieldMissing(entry.Name);
                }
                var error = ApplyToken(property, entry.Value, refs, 1);
                if (error is not null)
                {
                    return error;
                }
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return null;
        }

        private static Diagnostic? ApplyToken(
            SerializedProperty property, JToken token, RefResolver refs, int depth)
        {
            if (depth > MaxValuesDepth)
            {
                return ValueNotApplied(property, "nesting deeper than 3");
            }
            switch (property.propertyType)
            {
                case SerializedPropertyType.Enum:
                {
                    if (token is JValue { Type: JTokenType.String } name)
                    {
                        var names = property.enumNames;
                        for (var i = 0; i < names.Length; i++)
                        {
                            if (string.Equals(names[i], (string?)name.Value, StringComparison.Ordinal))
                            {
                                property.enumValueIndex = i;
                                return null;
                            }
                        }
                    }
                    return ValueNotApplied(property, "not an enum member name");
                }
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.Character:
                case SerializedPropertyType.RenderingLayerMask:
                    return ApplyInteger(property, token);
                case SerializedPropertyType.Boolean:
                    if (token is JValue { Type: JTokenType.Boolean } flag)
                    {
                        property.boolValue = (bool)flag.Value!;
                        return null;
                    }
                    return ValueNotApplied(property, "not a boolean");
                case SerializedPropertyType.Float:
                    if (token is JValue
                        { Type: JTokenType.Integer or JTokenType.Float } number)
                    {
                        var scalar = (double)number;
                        if (double.IsNaN(scalar) || double.IsInfinity(scalar))
                        {
                            return ValueNotApplied(property, "not a finite number");
                        }
                        if (property.numericType == SerializedPropertyNumericType.Double)
                        {
                            property.doubleValue = scalar;
                        }
                        else
                        {
                            property.floatValue = (float)scalar;
                        }
                        return null;
                    }
                    return ValueNotApplied(property, "not a number");
                case SerializedPropertyType.String:
                    if (token is JValue { Type: JTokenType.String } text)
                    {
                        property.stringValue = (string?)text.Value;
                        return null;
                    }
                    return ValueNotApplied(property, "not a string");
                case SerializedPropertyType.Color:
                    if (TryColor(token, out var color))
                    {
                        property.colorValue = color;
                        return null;
                    }
                    return ValueNotApplied(property, "not a Color");
                case SerializedPropertyType.Vector2:
                    if (TryVector(token, 2, out var v2))
                    {
                        property.vector2Value = new Vector2(v2[0], v2[1]);
                        return null;
                    }
                    return ValueNotApplied(property, "not a Vector2");
                case SerializedPropertyType.Vector3:
                    if (TryVector(token, 3, out var v3))
                    {
                        property.vector3Value = new Vector3(v3[0], v3[1], v3[2]);
                        return null;
                    }
                    return ValueNotApplied(property, "not a Vector3");
                case SerializedPropertyType.Vector4:
                    if (TryVector(token, 4, out var v4))
                    {
                        property.vector4Value =
                            new Vector4(v4[0], v4[1], v4[2], v4[3]);
                        return null;
                    }
                    return ValueNotApplied(property, "not a Vector4");
                case SerializedPropertyType.Quaternion:
                    if (TryVector(token, 4, out var q))
                    {
                        property.quaternionValue =
                            new Quaternion(q[0], q[1], q[2], q[3]);
                        return null;
                    }
                    return ValueNotApplied(property, "not a Quaternion");
                case SerializedPropertyType.Vector2Int:
                    if (TryInts(token, 2, out var i2))
                    {
                        property.vector2IntValue = new Vector2Int(i2[0], i2[1]);
                        return null;
                    }
                    return ValueNotApplied(property, "not a Vector2Int");
                case SerializedPropertyType.Vector3Int:
                    if (TryInts(token, 3, out var i3))
                    {
                        property.vector3IntValue = new Vector3Int(i3[0], i3[1], i3[2]);
                        return null;
                    }
                    return ValueNotApplied(property, "not a Vector3Int");
                case SerializedPropertyType.ObjectReference:
                {
                    if (token is JValue { Type: JTokenType.Null })
                    {
                        property.objectReferenceValue = null;
                        return null;
                    }
                    if (token is JObject reference)
                    {
                        var resolved = refs.Resolve<UnityEngine.Object>(
                            JsonValue.From(reference));
                        if (!resolved.Ok)
                        {
                            return resolved.Diagnostics[0];
                        }
                        property.objectReferenceValue = resolved.Value;
                        // The setter refuses an incompatible reference
                        // silently; the read-back is the assignment's proof.
                        return ReferenceEquals(property.objectReferenceValue, resolved.Value)
                            ? null
                            : ValueNotApplied(property, "an incompatible object reference");
                    }
                    return ValueNotApplied(property, "not an object reference");
                }
                case SerializedPropertyType.Generic:
                    if (property.isArray)
                    {
                        if (token is not JArray elements)
                        {
                            return ValueNotApplied(property, "not an array");
                        }
                        property.arraySize = elements.Count;
                        for (var i = 0; i < elements.Count; i++)
                        {
                            var error = ApplyToken(
                                property.GetArrayElementAtIndex(i),
                                elements[i]!,
                                refs,
                                depth + 1);
                            if (error is not null)
                            {
                                return error;
                            }
                        }
                        return null;
                    }
                    if (property.isFixedBuffer)
                    {
                        if (token is not JArray buffer ||
                            buffer.Count != property.fixedBufferSize)
                        {
                            return ValueNotApplied(property, "not a matching fixed buffer");
                        }
                        for (var i = 0; i < buffer.Count; i++)
                        {
                            var error = ApplyToken(
                                property.GetFixedBufferElementAtIndex(i),
                                buffer[i]!,
                                refs,
                                depth + 1);
                            if (error is not null)
                            {
                                return error;
                            }
                        }
                        return null;
                    }
                    if (token is JObject members)
                    {
                        foreach (var member in members.Properties())
                        {
                            var child = property.FindPropertyRelative(member.Name);
                            if (child is null)
                            {
                                return FieldMissing(property.propertyPath + "." + member.Name);
                            }
                            var error = ApplyToken(child, member.Value, refs, depth + 1);
                            if (error is not null)
                            {
                                return error;
                            }
                        }
                        return null;
                    }
                    return ValueNotApplied(property, "not a member object");
                default:
                    return ValueNotApplied(property, "an unsupported serialized property kind");
            }
        }

        // The integer family: unsigned numeric types take ulongValue with
        // their own bounds, everything else longValue — a JSON integer
        // outside the destination's range is refused instead of wrapping
        // silently the way the raw setter would.
        private static Diagnostic? ApplyInteger(SerializedProperty property, JToken token)
        {
            if (token is not JValue { Type: JTokenType.Integer } integer)
            {
                return ValueNotApplied(property, "not an integer");
            }
            var value = (long)integer.Value!;
            switch (property.numericType)
            {
                case SerializedPropertyNumericType.UInt8:
                    if (value < 0 || value > byte.MaxValue)
                    {
                        return ValueNotApplied(property, "outside the destination's range");
                    }
                    property.ulongValue = (ulong)value;
                    return null;
                case SerializedPropertyNumericType.UInt16:
                    if (value < 0 || value > ushort.MaxValue)
                    {
                        return ValueNotApplied(property, "outside the destination's range");
                    }
                    property.ulongValue = (ulong)value;
                    return null;
                case SerializedPropertyNumericType.UInt32:
                    if (value < 0 || value > uint.MaxValue)
                    {
                        return ValueNotApplied(property, "outside the destination's range");
                    }
                    property.ulongValue = (ulong)value;
                    return null;
                case SerializedPropertyNumericType.UInt64:
                    if (value < 0)
                    {
                        return ValueNotApplied(property, "outside the destination's range");
                    }
                    property.ulongValue = (ulong)value;
                    return null;
                case SerializedPropertyNumericType.Int8:
                    if (value < sbyte.MinValue || value > sbyte.MaxValue)
                    {
                        return ValueNotApplied(property, "outside the destination's range");
                    }
                    property.longValue = value;
                    return null;
                case SerializedPropertyNumericType.Int16:
                    if (value < short.MinValue || value > short.MaxValue)
                    {
                        return ValueNotApplied(property, "outside the destination's range");
                    }
                    property.longValue = value;
                    return null;
                case SerializedPropertyNumericType.Int32:
                    if (value < int.MinValue || value > int.MaxValue)
                    {
                        return ValueNotApplied(property, "outside the destination's range");
                    }
                    property.longValue = value;
                    return null;
                default:
                    property.longValue = value;
                    return null;
            }
        }

        private static bool TryColor(JToken token, out Color color)
        {
            color = default;
            if (token is JValue { Type: JTokenType.String } text &&
                text.Value is string hex)
            {
                return ColorUtility.TryParseHtmlString(hex, out color);
            }
            if (token is JObject obj &&
                NumericMembers(obj, "r", "g", "b", "a"))
            {
                color = new Color(
                    (float)(double)obj["r"]!,
                    (float)(double)obj["g"]!,
                    (float)(double)obj["b"]!,
                    (float)(double)obj["a"]!);
                return true;
            }
            return false;
        }

        private static bool TryVector(JToken token, int count, out float[] components)
        {
            components = Array.Empty<float>();
            if (token is not JObject obj)
            {
                return false;
            }
            var names = new[] { "x", "y", "z", "w" };
            if (!NumericMembers(obj, names, count))
            {
                return false;
            }
            components = new float[count];
            for (var i = 0; i < count; i++)
            {
                components[i] = (float)(double)obj[names[i]]!;
            }
            return true;
        }

        private static bool TryInts(JToken token, int count, out int[] components)
        {
            components = Array.Empty<int>();
            if (token is not JObject obj || obj.Count != count)
            {
                return false;
            }
            var names = new[] { "x", "y", "z" };
            components = new int[count];
            for (var i = 0; i < count; i++)
            {
                if (obj[names[i]] is not JValue { Type: JTokenType.Integer } member ||
                    (long)member.Value! < int.MinValue ||
                    (long)member.Value! > int.MaxValue)
                {
                    return false;
                }
                components[i] = (int)(long)member.Value!;
            }
            return true;
        }

        // Whether the object is exactly the first <paramref name="count"/>
        // named numeric members — no missing, no extras, no non-numbers.
        private static bool NumericMembers(
            JObject obj, string[] names, int count)
        {
            if (obj.Count != count)
            {
                return false;
            }
            for (var i = 0; i < count; i++)
            {
                if (obj[names[i]] is not JValue
                    { Type: JTokenType.Integer or JTokenType.Float } member ||
                    double.IsNaN((double)member) ||
                    double.IsInfinity((double)member))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool NumericMembers(JObject obj, params string[] names) =>
            NumericMembers(obj, names, names.Length);

        // ── Diagnostics ──────────────────────────────────────────────────

        private static bool Member(
            JObject obj, string name, JTokenType type, out JToken? value)
        {
            value = obj[name];
            return value is not null && value.Type == type;
        }

        private static ReconcileResult Unknown(string reason) =>
            new ReconcileResult.Unknown(reason);

        private static Diagnostic InvalidExtension() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_005,
                $"The asset path of port '{PathPort}' must carry the '{FileExtension}' extension.",
                port: PathPort,
                hint: "A ScriptableObject is serialized in the native .asset format.");

        private static Diagnostic TypeNotCreatable() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_006,
                $"The type named by port '{TypePort}' is not a creatable ScriptableObject type.",
                port: TypePort,
                hint: "Use the fully qualified name of a non-abstract type that derives from ScriptableObject.");

        private static Diagnostic InvalidValues() =>
            ErrorCodes.Create(
                ErrorCodes.OW_OP_006,
                $"Parameter '{ValuesPort}' must be a JSON object mapping serialized " +
                    "field names to the values to apply.",
                port: ValuesPort);

        private static Diagnostic FolderMissing() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_005,
                "The target folder does not exist.",
                port: PathPort,
                hint: "Create the folder first, or choose a folder that exists.");

        private static Diagnostic TargetOccupied() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_005,
                "The target asset already exists and is never overwritten.",
                port: PathPort,
                hint: "Choose a path that is free.");

        private static Diagnostic NotCreated() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_008,
                "The ScriptableObject asset could not be created at the target path.",
                port: PathPort);

        private static Diagnostic FieldMissing(string name) =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_007,
                $"Parameter '{ValuesPort}' names the field '{name}', which does not " +
                    "exist on the type.",
                port: ValuesPort,
                hint: "Only serialized fields of the created type can be set.");

        private static Diagnostic ValueNotApplied(SerializedProperty property, string why) =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_007,
                $"Parameter '{ValuesPort}' could not be applied to serialized field " +
                    $"'{property.propertyPath}': {why}.",
                port: ValuesPort);

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Creates a ScriptableObject asset of the named type with the given field values.")
            .Ja("ScriptableObject作成",
                "指定した型のScriptableObjectアセットを作成し、フィールド値を適用します。",
                "ScriptableObject", "作成", "アセット")
            .Input(PathPort, "unity.AssetPath", required: true)
            .Input(TypePort, "unity.TypeName", required: true)
            .Input(ValuesPort, "core.Json", required: false)
            .Output(AssetPort, "unity.AssetRef<unity.ScriptableObject>")
            .Effects(EffectNames.AssetCreate)
            .Undo(UndoSupport.None)
            .Idempotency(Idempotency.Reconcilable)
            // SPEC §I.3's named precondition and §G1 item 4: the type must
            // exist in the current domain (re-checked at run time after an
            // OW-VAL-015), and the write path's allowed-paths and
            // writability half is judged with the settings snapshot the
            // Run's acceptance validated (§H4.1, decision D-R6).
            .Requires("type_exists", TypePort)
            .Requires("path_writable", PathPort)
            .Build() with
            {
                DisplayName = "Create Scriptable Object",
                Description =
                    "Creates a ScriptableObject asset at the given project path of the " +
                    "fully qualified type and applies each entry of the values map to " +
                    "the type's serialized fields — scalars, enums by member name, " +
                    "colors, vectors, quaternions, object references and nested members " +
                    "to a depth of three. The path must carry the .asset extension and " +
                    "its parent folder must exist; an existing path fails with " +
                    "OW-UNITY-005 and is never overwritten, a missing or non-" +
                    "ScriptableObject type fails with OW-UNITY-006, and a field or " +
                    "value that cannot be applied fails with OW-UNITY-007. The " +
                    "Operation is Reconcilable: after an interruption it completes " +
                    "only when the artifact at the path is exactly the recorded type " +
                    "with the expected serialized content (SPEC §F5).",
                Category = "Asset",
                Tags = new[] { "asset", "scriptableobject" },
                Keywords = new[] { "scriptableobject", "asset", "create", "data" },
                Examples = new[]
                {
                    new OperationExample(
                        "Create a ScriptableObject data asset",
                        AssetOperationExamples.CreateScriptableObject),
                },
            };
    }
}
