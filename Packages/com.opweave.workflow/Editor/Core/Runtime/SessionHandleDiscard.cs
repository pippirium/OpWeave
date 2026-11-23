#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The SPEC §F4 item 4 session-key discard, driven by declared port
    /// types: when a resume crosses an Editor restart — a value's producing
    /// journal <c>session</c> differs from the current Editor session id —
    /// the <c>handle</c> keys that value's Ref members carry are dead
    /// (SPEC §B5: the session key is an InstanceID / EntityId that lives
    /// only as long as the Editor process). A stale handle must never
    /// resolve again: an id the new session reuses would silently name a
    /// different object, so the key is stripped wherever a rebuilt
    /// projection embeds it — the §E5 ValueStore values and the resolved
    /// Plan-side step inputs — leaving resolution to the persistent keys
    /// (guid, globalId, scene + path).
    /// </summary>
    /// <remarks>
    /// The discard is TYPE-directed, never shape-directed: a value loses
    /// session keys only when its declared port type is a Ref or a
    /// container of Refs (<c>core.List&lt;…&gt;</c>, nested included).
    /// A declared <c>core.Json</c>, a resume token or any other opaque
    /// position is never walked — a <c>{"kind": "asset", "handle": …}</c>
    /// object inside operation-authored data is data, not a Unity Ref,
    /// and rewriting it silently corrupts the value before it reaches the
    /// downstream Operation (T-W4.3-02). A port whose type cannot be
    /// resolved or proven Ref-bearing keeps its value verbatim. The
    /// journal itself is immutable — the discard applies to the in-memory
    /// projection the resumed Run drives, exactly like the session-side
    /// handle tables (<c>SessionState</c>) that the restart already
    /// dropped. Preflight snapshots are deliberately NOT touched: they
    /// are §F5 Reconcile evidence, and the reconciling Operation's own
    /// recorded session id is what decides <c>Unknown</c> there.
    /// </remarks>
    internal static class SessionHandleDiscard
    {
        private const string HandleMember = "handle";
        private const string KindMember = "kind";

        private static readonly HashSet<string> RefKinds =
            new HashSet<string>(StringComparer.Ordinal)
            {
                UnityShapeTypes.SceneKind,
                UnityShapeTypes.GameObjectKind,
                UnityShapeTypes.PrefabInstanceKind,
                UnityShapeTypes.ComponentKind,
                UnityShapeTypes.AssetKind,
                UnityShapeTypes.PrefabAssetKind,
            };

        /// <summary>
        /// The value without its Ref session keys when <paramref name="declaredType"/>
        /// can carry a Ref, or the same instance untouched: a Ref value's
        /// <c>handle</c> member dies with its session; a <c>core.Json</c>
        /// value or any other declared type keeps every member as data.
        /// Null value or null type in, same out.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="types"/> is null.</exception>
        internal static JsonValue? Strip(
            JsonValue? value, TypeRef? declaredType, ITypeRegistry types)
        {
            if (types is null)
            {
                throw new ArgumentNullException(nameof(types));
            }
            if (value is null || declaredType is null || !CarriesRef(declaredType, types))
            {
                return value;
            }
            var token = value.ToToken();
            return StripToken(token) ? JsonValue.From(token) : value;
        }

        /// <summary>
        /// The members of a <c>{port: value}</c> object stripped at the
        /// ports <paramref name="memberTypes"/> declares Ref-bearing — the
        /// discard on a journaled step's recorded outputs object — or the
        /// same instance when nothing was stripped. A member without a
        /// declared type is data and survives verbatim.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="memberTypes"/> or <paramref name="types"/> is null.
        /// </exception>
        internal static JsonValue? StripMembers(
            JsonValue? value,
            IReadOnlyDictionary<string, string> memberTypes,
            ITypeRegistry types)
        {
            if (memberTypes is null)
            {
                throw new ArgumentNullException(nameof(memberTypes));
            }
            if (types is null)
            {
                throw new ArgumentNullException(nameof(types));
            }
            if (value is null || value.ToToken() is not JObject members)
            {
                return value;
            }
            JObject? rewritten = null;
            foreach (var property in members.Properties())
            {
                if (!memberTypes.TryGetValue(property.Name, out var typeId))
                {
                    continue;
                }
                var member = JsonValue.From(property.Value);
                var stripped = StripByTypeId(member, typeId, types);
                if (!ReferenceEquals(stripped, member))
                {
                    rewritten ??= (JObject)members.DeepClone();
                    rewritten[property.Name] = stripped.ToToken();
                }
            }
            return rewritten is null ? value : JsonValue.From(rewritten);
        }

        /// <summary>
        /// The input dictionary stripped at the ports
        /// <paramref name="portTypes"/> declares Ref-bearing, or the same
        /// dictionary when nothing was stripped — the discard on resolved
        /// step inputs whose values were committed with the Run itself.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="portTypes"/> or <paramref name="types"/> is null.
        /// </exception>
        internal static IReadOnlyDictionary<string, InputValue>? StripInputs(
            IReadOnlyDictionary<string, InputValue>? inputs,
            IReadOnlyDictionary<string, string> portTypes,
            ITypeRegistry types)
        {
            if (portTypes is null)
            {
                throw new ArgumentNullException(nameof(portTypes));
            }
            if (types is null)
            {
                throw new ArgumentNullException(nameof(types));
            }
            if (inputs is null)
            {
                return null;
            }
            Dictionary<string, InputValue>? rewritten = null;
            foreach (var pair in inputs)
            {
                if (!pair.Value.IsPresent ||
                    !portTypes.TryGetValue(pair.Key, out var typeId))
                {
                    continue;
                }
                var stripped = StripByTypeId(pair.Value.Value!, typeId, types);
                if (!ReferenceEquals(stripped, pair.Value.Value))
                {
                    rewritten ??= new Dictionary<string, InputValue>(
                        inputs, StringComparer.Ordinal);
                    rewritten[pair.Key] = InputValue.Present(stripped);
                }
            }
            return rewritten ?? inputs;
        }

        // The declared type answers whether the value can carry a Ref at
        // all: the resolved definition's Kind is Ref, or it is a list whose
        // element type (recursively) is — core.List<Ref> and deeper nestings
        // still embed live session keys. A type the registry cannot resolve,
        // and a 'type:' class-id leaf that is a generic argument rather
        // than a type, prove nothing and keep the value.
        private static bool CarriesRef(TypeRef type, ITypeRegistry types)
        {
            if (type.Name.StartsWith(TypeRef.ClassRefPrefix, StringComparison.Ordinal))
            {
                return false;
            }
            var resolved = types.Resolve(type);
            if (!resolved.Ok || resolved.Value is null)
            {
                return false;
            }
            if (resolved.Value.Kind == TypeKind.Ref)
            {
                return true;
            }
            return resolved.Value.Kind == TypeKind.List &&
                type.Args.Count == 1 && CarriesRef(type.Args[0], types);
        }

        // A declared Type ID string resolves to a TypeRef or proves
        // nothing — a malformed recorded type keeps the value as data.
        private static JsonValue StripByTypeId(
            JsonValue value, string typeId, ITypeRegistry types)
        {
            var parsed = TypeRef.Parse(typeId);
            return parsed.Ok && parsed.Value is { } type
                ? Strip(value, type, types)!
                : value;
        }

        // Recursive walk inside a declared Ref-bearing value: a Ref object
        // loses its 'handle' member, and every nested object/array is
        // visited — a ComponentRef's owner object, a Ref inside a list
        // output — while scalars and non-Ref 'handle' members pass through
        // untouched.
        private static bool StripToken(JToken token)
        {
            var changed = false;
            if (token is JObject obj)
            {
                if (obj[HandleMember] is not null &&
                    obj[KindMember] is JValue { Type: JTokenType.String } kind &&
                    kind.Value is string discriminator && RefKinds.Contains(discriminator))
                {
                    obj.Remove(HandleMember);
                    changed = true;
                }
                foreach (var property in obj.Properties())
                {
                    changed |= StripToken(property.Value);
                }
            }
            else if (token is JArray array)
            {
                foreach (var item in array)
                {
                    changed |= StripToken(item);
                }
            }
            return changed;
        }
    }
}
