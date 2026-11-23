#nullable enable

using System;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;

namespace OpWeave.Operations
{
    /// <summary>
    /// The single JSON ↔ <see cref="SerializedProperty"/> conversion of the
    /// SPEC §G5 item 7 supported set — the private helper of
    /// <c>unity.property.get</c>/<c>unity.property.set</c> (T-W2.5-04) and
    /// their tests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The supported kinds are the SPEC §G5 item 7 list — Integer, Boolean,
    /// Float, String, Enum (by member name), Color, Vector2/3/4, Quaternion,
    /// ObjectReference (a §B5 Ref JSON resolved through G2), arrays and
    /// Generic member objects — plus the same local extensions
    /// <see cref="CreateScriptableObjectOperation"/> documented for its
    /// <c>values</c> port: the integer numeric family (byte, LayerMask,
    /// Character, RenderingLayerMask) and Vector2Int/3Int. Anything else —
    /// a missing member, an unapplyable value, an unsupported kind such as
    /// Bounds, Rect or a managed reference, and nesting deeper than
    /// <see cref="MaxDepth"/> — is OW-UNITY-007, the §J property-operation
    /// failure, and nothing is committed (the caller decides whether to
    /// apply the modified properties at all).
    /// </para>
    /// <para>
    /// <see cref="CreateScriptableObjectOperation"/> carries a private copy
    /// of the same conversion for its <c>values</c> port — that file belongs
    /// to the <c>unity.asset.*</c> claim (T-W2.7), so the duplication is
    /// deliberate. Once the asset ticket can share this helper the two
    /// copies are candidates for a single consolidation.
    /// </para>
    /// </remarks>
    internal static class SerializedPropertyValues
    {
        /// <summary>
        /// The §G5 item 7 bound on nested application: the leaf property sits
        /// at most three levels deep — the property path's segment count for
        /// <c>unity.property.*</c> and the member-object nesting inside a
        /// value applied there.
        /// </summary>
        internal const int MaxDepth = 3;

        /// <summary>
        /// Applies <paramref name="token"/> to <paramref name="property"/> —
        /// a JSON scalar to a scalar property, a member-name string to an
        /// enum, a component object to a vector/color/quaternion, a Ref JSON
        /// object or null to an object reference, an array to an array and a
        /// member object to a Generic structure. <paramref name="depth"/> is
        /// the property's own nesting level (1 for the property the path
        /// names); nested members recurse one level deeper and refuse past
        /// <see cref="MaxDepth"/>. <paramref name="port"/> names the input
        /// port the OW-UNITY-007 diagnostic reports.
        /// </summary>
        /// <returns>
        /// <see langword="null"/> when the value applied; the OW-UNITY-007
        /// diagnostic otherwise — the property is then left half-written at
        /// most, never committed, because the caller applies modified
        /// properties only on success.
        /// </returns>
        internal static Diagnostic? Apply(
            SerializedProperty property,
            JToken token,
            RefResolver refs,
            int depth,
            string port)
        {
            if (depth > MaxDepth)
            {
                return ValueNotApplied(port, property, "nesting deeper than 3");
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
                    return ValueNotApplied(port, property, "not an enum member name");
                }
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.Character:
                case SerializedPropertyType.RenderingLayerMask:
                    return ApplyInteger(port, property, token);
                case SerializedPropertyType.Boolean:
                    if (token is JValue { Type: JTokenType.Boolean } flag)
                    {
                        property.boolValue = (bool)flag.Value!;
                        return null;
                    }
                    return ValueNotApplied(port, property, "not a boolean");
                case SerializedPropertyType.Float:
                    if (token is JValue
                        { Type: JTokenType.Integer or JTokenType.Float } number)
                    {
                        var scalar = (double)number;
                        if (double.IsNaN(scalar) || double.IsInfinity(scalar))
                        {
                            return ValueNotApplied(port, property, "not a finite number");
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
                    return ValueNotApplied(port, property, "not a number");
                case SerializedPropertyType.String:
                    if (token is JValue { Type: JTokenType.String } text)
                    {
                        property.stringValue = (string?)text.Value;
                        return null;
                    }
                    return ValueNotApplied(port, property, "not a string");
                case SerializedPropertyType.Color:
                    if (TryColor(token, out var color))
                    {
                        property.colorValue = color;
                        return null;
                    }
                    return ValueNotApplied(port, property, "not a Color");
                case SerializedPropertyType.Vector2:
                    if (TryVector(token, 2, out var v2))
                    {
                        property.vector2Value = new Vector2(v2[0], v2[1]);
                        return null;
                    }
                    return ValueNotApplied(port, property, "not a Vector2");
                case SerializedPropertyType.Vector3:
                    if (TryVector(token, 3, out var v3))
                    {
                        property.vector3Value = new Vector3(v3[0], v3[1], v3[2]);
                        return null;
                    }
                    return ValueNotApplied(port, property, "not a Vector3");
                case SerializedPropertyType.Vector4:
                    if (TryVector(token, 4, out var v4))
                    {
                        property.vector4Value =
                            new Vector4(v4[0], v4[1], v4[2], v4[3]);
                        return null;
                    }
                    return ValueNotApplied(port, property, "not a Vector4");
                case SerializedPropertyType.Quaternion:
                    if (TryVector(token, 4, out var q))
                    {
                        property.quaternionValue =
                            new Quaternion(q[0], q[1], q[2], q[3]);
                        return null;
                    }
                    return ValueNotApplied(port, property, "not a Quaternion");
                case SerializedPropertyType.Vector2Int:
                    if (TryInts(token, 2, out var i2))
                    {
                        property.vector2IntValue = new Vector2Int(i2[0], i2[1]);
                        return null;
                    }
                    return ValueNotApplied(port, property, "not a Vector2Int");
                case SerializedPropertyType.Vector3Int:
                    if (TryInts(token, 3, out var i3))
                    {
                        property.vector3IntValue = new Vector3Int(i3[0], i3[1], i3[2]);
                        return null;
                    }
                    return ValueNotApplied(port, property, "not a Vector3Int");
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
                            : ValueNotApplied(port, property, "an incompatible object reference");
                    }
                    return ValueNotApplied(port, property, "not an object reference");
                }
                case SerializedPropertyType.Generic:
                    if (property.isArray)
                    {
                        if (token is not JArray elements)
                        {
                            return ValueNotApplied(port, property, "not an array");
                        }
                        property.arraySize = elements.Count;
                        for (var i = 0; i < elements.Count; i++)
                        {
                            var error = Apply(
                                property.GetArrayElementAtIndex(i),
                                elements[i]!,
                                refs,
                                depth + 1,
                                port);
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
                            return ValueNotApplied(port, property, "not a matching fixed buffer");
                        }
                        for (var i = 0; i < buffer.Count; i++)
                        {
                            var error = Apply(
                                property.GetFixedBufferElementAtIndex(i),
                                buffer[i]!,
                                refs,
                                depth + 1,
                                port);
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
                                return FieldMissing(port, property.propertyPath + "." + member.Name);
                            }
                            var error = Apply(child, member.Value, refs, depth + 1, port);
                            if (error is not null)
                            {
                                return error;
                            }
                        }
                        return null;
                    }
                    return ValueNotApplied(port, property, "not a member object");
                default:
                    return ValueNotApplied(port, property, "an unsupported serialized property kind");
            }
        }

        /// <summary>
        /// Reads <paramref name="property"/> back as the JSON the
        /// <see cref="Apply"/> side would accept — the same §G5 item 7
        /// supported set: scalars to JSON scalars, an enum to its member
        /// name (its integer value when the stored index names no member —
        /// a flags combination has no single name), vectors, colors and
        /// quaternions to their component objects, an object reference to
        /// the §G2 Ref JSON of <c>unity.ObjectRef</c> (an explicit JSON null
        /// when it is empty), an array to a JSON array and a Generic member
        /// to a member object. <paramref name="depth"/> is the property's
        /// own nesting level (1 for the property the path names); array
        /// elements and Generic members recurse one level deeper, and a
        /// member tree that would reach past <see cref="MaxDepth"/> fails
        /// with OW-UNITY-007 — the read stays inside the bound the apply
        /// side enforces, so a successful value always applies back. An
        /// unsupported kind or a value JSON cannot express (a non-finite
        /// number) fails with OW-UNITY-007 instead of being approximated.
        /// </summary>
        internal static Result<JToken> Read(
            SerializedProperty property, RefResolver refs, int depth, string port)
        {
            if (depth > MaxDepth)
            {
                return Result<JToken>.Failure(ValueNotApplied(
                    port, property, "nesting deeper than 3"));
            }
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.Character:
                case SerializedPropertyType.RenderingLayerMask:
                    switch (property.numericType)
                    {
                        case SerializedPropertyNumericType.UInt64:
                        {
                            var wide = property.ulongValue;
                            return wide > long.MaxValue
                                ? Result<JToken>.Failure(ValueNotApplied(
                                    port, property, "outside the Int64 range"))
                                : Result<JToken>.Success(new JValue((long)wide));
                        }
                        case SerializedPropertyNumericType.UInt8:
                        case SerializedPropertyNumericType.UInt16:
                        case SerializedPropertyNumericType.UInt32:
                            return Result<JToken>.Success(new JValue((long)property.ulongValue));
                        default:
                            return Result<JToken>.Success(new JValue(property.longValue));
                    }
                case SerializedPropertyType.Enum:
                {
                    var index = property.enumValueIndex;
                    var names = property.enumNames;
                    return index >= 0 && index < names.Length
                        ? Result<JToken>.Success(new JValue(names[index]))
                        : Result<JToken>.Success(new JValue(property.longValue));
                }
                case SerializedPropertyType.Boolean:
                    return Result<JToken>.Success(new JValue(property.boolValue));
                case SerializedPropertyType.Float:
                {
                    var scalar = property.numericType == SerializedPropertyNumericType.Double
                        ? property.doubleValue
                        : (double)property.floatValue;
                    return double.IsNaN(scalar) || double.IsInfinity(scalar)
                        ? Result<JToken>.Failure(ValueNotApplied(
                            port, property, "not a finite number"))
                        : Result<JToken>.Success(new JValue(scalar));
                }
                case SerializedPropertyType.String:
                    return Result<JToken>.Success(new JValue(property.stringValue));
                case SerializedPropertyType.Color:
                {
                    var c = property.colorValue;
                    return Result<JToken>.Success((JToken)new JObject
                    {
                        ["r"] = c.r,
                        ["g"] = c.g,
                        ["b"] = c.b,
                        ["a"] = c.a,
                    });
                }
                case SerializedPropertyType.Vector2:
                {
                    var v = property.vector2Value;
                    return Result<JToken>.Success((JToken)new JObject
                    {
                        ["x"] = v.x,
                        ["y"] = v.y,
                    });
                }
                case SerializedPropertyType.Vector3:
                {
                    var v = property.vector3Value;
                    return Result<JToken>.Success((JToken)new JObject
                    {
                        ["x"] = v.x,
                        ["y"] = v.y,
                        ["z"] = v.z,
                    });
                }
                case SerializedPropertyType.Vector4:
                {
                    var v = property.vector4Value;
                    return Result<JToken>.Success((JToken)new JObject
                    {
                        ["x"] = v.x,
                        ["y"] = v.y,
                        ["z"] = v.z,
                        ["w"] = v.w,
                    });
                }
                case SerializedPropertyType.Quaternion:
                {
                    var q = property.quaternionValue;
                    return Result<JToken>.Success((JToken)new JObject
                    {
                        ["x"] = q.x,
                        ["y"] = q.y,
                        ["z"] = q.z,
                        ["w"] = q.w,
                    });
                }
                case SerializedPropertyType.Vector2Int:
                {
                    var v = property.vector2IntValue;
                    return Result<JToken>.Success((JToken)new JObject
                    {
                        ["x"] = v.x,
                        ["y"] = v.y,
                    });
                }
                case SerializedPropertyType.Vector3Int:
                {
                    var v = property.vector3IntValue;
                    return Result<JToken>.Success((JToken)new JObject
                    {
                        ["x"] = v.x,
                        ["y"] = v.y,
                        ["z"] = v.z,
                    });
                }
                case SerializedPropertyType.ObjectReference:
                {
                    var reference = property.objectReferenceValue;
                    if (reference == null)
                    {
                        return Result<JToken>.Success(JValue.CreateNull());
                    }
                    // The §G2 writer decides the Ref shape — the same rule a
                    // core.Json input accepts on the Apply side.
                    JsonValue written;
                    try
                    {
                        written = refs.ToRef(reference, "unity.ObjectRef");
                    }
                    catch (Exception)
                    {
                        return Result<JToken>.Failure(ValueNotApplied(
                            port, property, "an object reference that cannot be written as a Ref"));
                    }
                    return Result<JToken>.Success(written.ToToken());
                }
                case SerializedPropertyType.Generic:
                    if (property.isArray)
                    {
                        var elements = new JArray();
                        for (var i = 0; i < property.arraySize; i++)
                        {
                            var element = Read(
                                property.GetArrayElementAtIndex(i),
                                refs, depth + 1, port);
                            if (!element.Ok)
                            {
                                return Result<JToken>.Failure(element.Diagnostics);
                            }
                            elements.Add(element.Value!);
                        }
                        return Result<JToken>.Success(elements);
                    }
                    if (property.isFixedBuffer)
                    {
                        var elements = new JArray();
                        for (var i = 0; i < property.fixedBufferSize; i++)
                        {
                            var element = Read(
                                property.GetFixedBufferElementAtIndex(i),
                                refs, depth + 1, port);
                            if (!element.Ok)
                            {
                                return Result<JToken>.Failure(element.Diagnostics);
                            }
                            elements.Add(element.Value!);
                        }
                        return Result<JToken>.Success(elements);
                    }
                    return ReadGenericMembers(property, refs, depth, port);
                default:
                    return Result<JToken>.Failure(ValueNotApplied(
                        port, property, "an unsupported serialized property kind"));
            }
        }

        // The member object of a Generic leaf: each serialized child's name
        // mapped to its value. The child walk stops where the parent's own
        // children end — on Unity 6000 the GetEndProperty sentinel does not
        // bound every layout, so the depth comparison is the real stop and
        // the sentinel stays the second one (the normalizer's precedent).
        private static Result<JToken> ReadGenericMembers(
            SerializedProperty parent, RefResolver refs, int depth, string port)
        {
            var members = new JObject();
            var parentDepth = parent.depth;
            var iterator = parent.Copy();
            var end = iterator.GetEndProperty();
            if (!iterator.Next(enterChildren: true))
            {
                return Result<JToken>.Success(members);
            }
            while (iterator.depth > parentDepth &&
                   !SerializedProperty.EqualContents(iterator, end))
            {
                var member = Read(iterator, refs, depth + 1, port);
                if (!member.Ok)
                {
                    return Result<JToken>.Failure(member.Diagnostics);
                }
                members[iterator.name] = member.Value!;
                if (!iterator.Next(enterChildren: false))
                {
                    break;
                }
            }
            return Result<JToken>.Success(members);
        }

        // The integer family: unsigned numeric types take ulongValue with
        // their own bounds, everything else longValue — a JSON integer
        // outside the destination's range is refused instead of wrapping
        // silently the way the raw setter would.
        private static Diagnostic? ApplyInteger(
            string port, SerializedProperty property, JToken token)
        {
            if (token is not JValue { Type: JTokenType.Integer } integer)
            {
                return ValueNotApplied(port, property, "not an integer");
            }
            var value = (long)integer.Value!;
            switch (property.numericType)
            {
                case SerializedPropertyNumericType.UInt8:
                    if (value < 0 || value > byte.MaxValue)
                    {
                        return ValueNotApplied(port, property, "outside the destination's range");
                    }
                    property.ulongValue = (ulong)value;
                    return null;
                case SerializedPropertyNumericType.UInt16:
                    if (value < 0 || value > ushort.MaxValue)
                    {
                        return ValueNotApplied(port, property, "outside the destination's range");
                    }
                    property.ulongValue = (ulong)value;
                    return null;
                case SerializedPropertyNumericType.UInt32:
                    if (value < 0 || value > uint.MaxValue)
                    {
                        return ValueNotApplied(port, property, "outside the destination's range");
                    }
                    property.ulongValue = (ulong)value;
                    return null;
                case SerializedPropertyNumericType.UInt64:
                    if (value < 0)
                    {
                        return ValueNotApplied(port, property, "outside the destination's range");
                    }
                    property.ulongValue = (ulong)value;
                    return null;
                case SerializedPropertyNumericType.Int8:
                    if (value < sbyte.MinValue || value > sbyte.MaxValue)
                    {
                        return ValueNotApplied(port, property, "outside the destination's range");
                    }
                    property.longValue = value;
                    return null;
                case SerializedPropertyNumericType.Int16:
                    if (value < short.MinValue || value > short.MaxValue)
                    {
                        return ValueNotApplied(port, property, "outside the destination's range");
                    }
                    property.longValue = value;
                    return null;
                case SerializedPropertyNumericType.Int32:
                    if (value < int.MinValue || value > int.MaxValue)
                    {
                        return ValueNotApplied(port, property, "outside the destination's range");
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

        private static Diagnostic FieldMissing(string port, string name) =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_007,
                $"Parameter '{port}' names the field '{name}', which does not " +
                    "exist on the type.",
                port: port,
                hint: "Only serialized fields of the target can be set.");

        private static Diagnostic ValueNotApplied(
            string port, SerializedProperty property, string why) =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_007,
                $"Parameter '{port}' could not be applied to serialized field " +
                    $"'{property.propertyPath}': {why}.",
                port: port);
    }
}
