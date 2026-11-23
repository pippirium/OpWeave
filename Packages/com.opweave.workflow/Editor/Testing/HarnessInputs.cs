#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpWeave.Testing
{
    /// <summary>
    /// Turns the anonymous object (or JSON bag) a harness caller passes for a
    /// run's <c>inputs</c> (SPEC §M.6 item 1) into the SPEC §F1 values the step
    /// consumes — the form a Plan-bound run would arrive in after the compiler
    /// converted each literal with the port's Codec.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Members are matched to declared input ports by name, Ordinal, exactly as
    /// the Plan binds a parameter to a port; a member no port declares is
    /// OW-OP-005 and a required port left without a value is OW-RUN-007 (SPEC §J
    /// — the same answers the Runner gives for the same two mistakes).
    /// </para>
    /// <para>
    /// Value forms are deliberately narrow, because the harness must not invent
    /// a serialization the Codec would not have produced: <see langword="null"/>
    /// is a present JSON null (SPEC §0.4 keeps it apart from a port the caller
    /// said nothing about, which stays Missing), a <see cref="JsonValue"/> or
    /// <see cref="JToken"/> travels verbatim, a <see cref="string"/>,
    /// <see cref="bool"/> or number becomes that JSON scalar, and a
    /// <see cref="UnityEngine.Object"/> or <see cref="Scene"/> becomes the
    /// SPEC §G2 ref of the port's own Type ID. Anything else — a
    /// <c>Vector3</c>, a <c>DateTime</c>, an enum — is refused with OW-RUN-007
    /// rather than guessed: pass it as a <see cref="JsonValue"/> to pin the
    /// exact JSON shape the Codec will see.
    /// </para>
    /// </remarks>
    internal static class HarnessInputs
    {
        /// <summary>
        /// Builds the per-port input map of one run. Every declared port is
        /// present in the result — one the caller said nothing about as
        /// <see cref="InputValue.Missing"/>, which is how a step distinguishes
        /// "absent" from an explicit JSON null.
        /// </summary>
        internal static Result<Dictionary<string, InputValue>> Build(
            OperationDescriptor descriptor, object? inputs, string nodeId)
        {
            var map = new Dictionary<string, InputValue>(StringComparer.Ordinal);
            foreach (var port in descriptor.Inputs)
            {
                map[port.Name] = InputValue.Missing;
            }

            var diagnostics = new List<Diagnostic>();
            var bag = PropertyBag(inputs);
            var names = new List<string>(bag.Keys);
            names.Sort(StringComparer.Ordinal);
            foreach (var name in names)
            {
                var port = FindPort(descriptor, name);
                if (port is null)
                {
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_OP_005,
                        $"Operation '{descriptor.Id}' has no input port '{name}'.",
                        nodeId: nodeId,
                        port: name,
                        hint: "Name every input member after a declared input port."));
                    continue;
                }
                var converted = Convert(bag[name], port, descriptor, nodeId);
                if (converted.Ok && converted.Value is { } value)
                {
                    map[port.Name] = value;
                }
                else
                {
                    diagnostics.AddRange(converted.Diagnostics);
                }
            }
            foreach (var port in descriptor.Inputs)
            {
                if (port.Required && !map[port.Name].IsPresent)
                {
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_RUN_007,
                        $"Input port '{port.Name}' of Operation '{descriptor.Id}' " +
                            "has no value.",
                        nodeId: nodeId,
                        port: port.Name,
                        hint: "Pass a value for every required input port."));
                }
            }
            if (diagnostics.Count != 0)
            {
                return Result<Dictionary<string, InputValue>>.Failure(diagnostics);
            }
            return Result<Dictionary<string, InputValue>>.Success(map);
        }

        /// <summary>
        /// The inputs in their JSON form — what SPEC §M.6 item 2 feeds the
        /// input-side Codec check with, where a value that is not a
        /// <see cref="JsonValue"/> already is the question the contract asks.
        /// A <see cref="UnityEngine.Object"/> becomes its generic
        /// <c>unity.ObjectRef</c> and any other form Newtonsoft's default
        /// object contract, so the Codec — never the harness — decides whether
        /// that shape is the Type ID's.
        /// </summary>
        internal static IReadOnlyDictionary<string, JsonValue> ToJsonInputs(object? inputs)
        {
            var result = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            var bag = PropertyBag(inputs);
            var names = new List<string>(bag.Keys);
            names.Sort(StringComparer.Ordinal);
            foreach (var name in names)
            {
                var value = bag[name];
                if (value is JsonValue json)
                {
                    result[name] = json;
                }
                else if (value is JToken token)
                {
                    result[name] = JsonValue.From(token);
                }
                else if (value is null)
                {
                    result[name] = JsonValue.From(JValue.CreateNull());
                }
                else if (value is UnityEngine.Object unityObject)
                {
                    result[name] = new RefResolver().ToRef(unityObject, "unity.ObjectRef");
                }
                else if (value is Scene scene)
                {
                    result[name] = SceneRefOf(scene);
                }
                else
                {
                    var raw = RawValue(value);
                    result[name] = raw is not null
                        ? JsonValue.From(raw)
                        : JsonValue.From(JToken.FromObject(value));
                }
            }
            return result;
        }

        /// <summary>Declared input port name → Type ID.</summary>
        internal static IReadOnlyDictionary<string, string> InputTypes(
            OperationDescriptor descriptor)
        {
            var types = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var port in descriptor.Inputs)
            {
                types[port.Name] = port.Type;
            }
            return types;
        }

        /// <summary>Declared output port name → Type ID.</summary>
        internal static IReadOnlyDictionary<string, string> OutputTypes(
            OperationDescriptor descriptor)
        {
            var types = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var port in descriptor.Outputs)
            {
                types[port.Name] = port.Type;
            }
            return types;
        }

        private static Result<InputValue> Convert(
            object? value,
            PortDescriptor port,
            OperationDescriptor descriptor,
            string nodeId)
        {
            if (value is null)
            {
                return Result<InputValue>.Success(
                    InputValue.Present(JsonValue.From(JValue.CreateNull())));
            }
            if (value is JsonValue json)
            {
                return Result<InputValue>.Success(InputValue.Present(json));
            }
            if (value is JToken token)
            {
                return Result<InputValue>.Success(InputValue.Present(JsonValue.From(token)));
            }
            if (value is Scene scene)
            {
                return Result<InputValue>.Success(InputValue.Present(SceneRefOf(scene)));
            }
            if (value is UnityEngine.Object unityObject)
            {
                var refTypeId = RefTypeIdFor(port, unityObject);
                if (refTypeId is null)
                {
                    return Unconvertible(port, descriptor, nodeId,
                        $"the port type '{port.Type}' is not a SPEC §B5 ref type");
                }
                try
                {
                    return Result<InputValue>.Success(
                        InputValue.Present(new RefResolver().ToRef(unityObject, refTypeId)));
                }
                catch (Exception exception)
                {
                    return Unconvertible(port, descriptor, nodeId, exception.Message);
                }
            }
            var raw = RawValue(value);
            if (raw is not null)
            {
                return Result<InputValue>.Success(InputValue.Present(JsonValue.From(raw)));
            }
            return Unconvertible(port, descriptor, nodeId,
                $"a {value.GetType().Name} value is not a JSON form the harness accepts");
        }

        private static Result<InputValue> Unconvertible(
            PortDescriptor port,
            OperationDescriptor descriptor,
            string nodeId,
            string reason) =>
            Result<InputValue>.Failure(ErrorCodes.Create(
                ErrorCodes.OW_RUN_007,
                $"The value given for input port '{port.Name}' of Operation " +
                    $"'{descriptor.Id}' is not a value of type '{port.Type}': {reason}.",
                nodeId: nodeId,
                port: port.Name,
                hint: "Pass a JsonValue, a string, bool or number, a UnityEngine.Object " +
                    "or Scene for a ref-typed port, or null for an explicit JSON null."));

        // The SPEC §B5 ref Type ID that carries a value of the port's declared
        // Type ID: the port's own id when it already is a ref type — verbatim,
        // so a closed generic keeps its argument — the §G2 ref of the plain
        // Unity class it names otherwise. RefResolver is what decides whether
        // the (object, ref type) pair actually fits.
        private static string? RefTypeIdFor(PortDescriptor port, UnityEngine.Object value)
        {
            var parsed = TypeRef.Parse(port.Type);
            if (!parsed.Ok || parsed.Value is null)
            {
                return null;
            }
            var name = parsed.Value.Name;
            if (name.EndsWith("Ref", StringComparison.Ordinal))
            {
                return port.Type;
            }
            switch (name)
            {
                case "unity.Object":
                    return "unity.ObjectRef";
                case "unity.Scene":
                    return "unity.SceneRef";
                case "unity.GameObject":
                    return "unity.GameObjectRef";
                case "unity.PrefabInstance":
                    return "unity.PrefabInstanceRef";
                case "unity.PrefabContents":
                    return "unity.PrefabAssetRef";
                default:
                    // A plain Unity class port takes the ref form of its own
                    // Type ID — ComponentRef<T> for a Component, AssetRef<T>
                    // for an asset object (SPEC §B5).
                    if (!name.StartsWith("unity.", StringComparison.Ordinal))
                    {
                        return null;
                    }
                    return value is Component
                        ? "unity.ComponentRef<" + name + ">"
                        : "unity.AssetRef<" + name + ">";
            }
        }

        // SPEC §G2: a scene that was never saved is referenced by an empty path
        // and an empty guid; a saved one by the canonical ref of its scene asset.
        private static JsonValue SceneRefOf(Scene scene)
        {
            if (string.IsNullOrEmpty(scene.path))
            {
                return JsonValue.From(new JObject
                {
                    ["path"] = string.Empty,
                    ["guid"] = string.Empty,
                });
            }
            var asset = AssetDatabase.LoadAssetAtPath<SceneAsset>(scene.path);
            if (asset is null)
            {
                return JsonValue.From(new JObject
                {
                    ["path"] = scene.path,
                    ["guid"] = string.Empty,
                });
            }
            return new RefResolver().ToRef(asset, "unity.SceneRef");
        }

        // The JSON scalar a CLR scalar stands for. An enum is refused rather
        // than read through its underlying type, because §B2 lets an enum type
        // name its members as strings and the harness must not pick a form the
        // Codec might reject.
        private static JToken? RawValue(object value)
        {
            if (value is Enum)
            {
                return null;
            }
            switch (System.Convert.GetTypeCode(value))
            {
                case TypeCode.String:
                    return new JValue((string)value);
                case TypeCode.Boolean:
                    return new JValue((bool)value);
                case TypeCode.Char:
                    return new JValue(value.ToString());
                case TypeCode.SByte:
                case TypeCode.Byte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Decimal:
                    return new JValue(value);
                default:
                    return null;
            }
        }

        // The caller's input members. An anonymous object or a POCO is read
        // through its public instance properties; a dictionary or a JSON object
        // is already name → value; null supplies nothing.
        private static Dictionary<string, object?> PropertyBag(object? inputs)
        {
            var bag = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (inputs is null)
            {
                return bag;
            }
            if (inputs is JsonValue json)
            {
                if (json.ToToken() is JObject jsonObject)
                {
                    foreach (var property in jsonObject.Properties())
                    {
                        bag[property.Name] = JsonValue.From(property.Value);
                    }
                }
                return bag;
            }
            if (inputs is JObject token)
            {
                foreach (var property in token.Properties())
                {
                    bag[property.Name] = JsonValue.From(property.Value);
                }
                return bag;
            }
            if (inputs is IReadOnlyDictionary<string, object?> readOnly)
            {
                foreach (var pair in readOnly)
                {
                    bag[pair.Key] = pair.Value;
                }
                return bag;
            }
            if (inputs is IDictionary<string, object?> pairs)
            {
                foreach (var pair in pairs)
                {
                    bag[pair.Key] = pair.Value;
                }
                return bag;
            }
            foreach (var property in inputs.GetType().GetProperties(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0)
                {
                    continue;
                }
                object? value;
                try
                {
                    value = property.GetValue(inputs, null);
                }
                catch (Exception)
                {
                    // A getter that throws supplies nothing; if its port is
                    // required the run is refused OW-RUN-007 like any other
                    // missing required input.
                    continue;
                }
                bag[property.Name] = value;
            }
            return bag;
        }

        private static PortDescriptor? FindPort(
            OperationDescriptor descriptor, string name)
        {
            foreach (var port in descriptor.Inputs)
            {
                if (string.Equals(port.Name, name, StringComparison.Ordinal))
                {
                    return port;
                }
            }
            return null;
        }
    }
}
