#nullable enable

using System;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The session <c>handle</c> key of SPEC §B5 — <c>InstanceID</c>
    /// (<see cref="int"/>) on Editors below 6000.5 and <see cref="EntityId"/>
    /// on 6000.5+, switched by conditional compilation exactly as SPEC §G2
    /// and §T.4 L-04 prescribe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The L-04 probe (docs/research/probes/L-04.md) measured every fact this
    /// type relies on: on 6000.5 the whole InstanceID family is
    /// <c>[Obsolete(error=true)]</c>, so the <see cref="EntityId"/> switch is
    /// not merely preferred but forced; on 6000.3–6000.4 the InstanceID
    /// calls still compile but are <c>[Obsolete]</c> warning-level, which is
    /// suppressed locally here (the EntityId-as-handle switch stays at
    /// 6000.5 where SPEC puts it). On every generation the handle survives a
    /// domain reload and is session-scoped only — it is discarded across an
    /// Editor restart (SPEC §B5).
    /// </para>
    /// <para>
    /// A handle is read as an unsigned 64-bit key so both generations share
    /// one comparison space: below 6000.5 it is the <c>uint</c> bit pattern
    /// of the <see cref="int"/> InstanceID, on 6000.5+ it is the raw
    /// <c>ulong</c> of <see cref="EntityId"/>. A null resolve — not
    /// <c>EntityId.IsValid</c> — is the failure signal (L-04:
    /// <c>IsValid</c> does not prove resolvability).
    /// </para>
    /// </remarks>
    internal static class SessionHandle
    {
        /// <summary>
        /// Reads a <c>handle</c> member into the session's key space.
        /// Accepts the Codec's two spellings (SPEC §B5): a JSON integer and a
        /// decimal-digit string. Below 6000.5 both spellings of the int key
        /// space are accepted — the signed InstanceID (<c>-1338</c>) and its
        /// <see cref="uint"/> bit pattern (<c>4294965958</c>), which decode to
        /// the same session key. A token of any other JSON type, or a value
        /// outside this Editor generation's handle range, is not a handle of
        /// this session and returns <see langword="false"/>.
        /// </summary>
        internal static bool TryRead(JToken token, out ulong raw)
        {
            raw = 0;
            if (token is not JValue value)
            {
                return false;
            }
            if (value.Type == JTokenType.Integer)
            {
                var integer = (long)value.Value!;
#if UNITY_6000_5_OR_NEWER
                raw = unchecked((ulong)integer);
                return true;
#else
                if (integer < int.MinValue || integer > uint.MaxValue)
                {
                    return false;
                }
                raw = unchecked((ulong)(uint)integer);
                return true;
#endif
            }
            if (value.Type == JTokenType.String)
            {
                var text = (string?)value.Value;
#if UNITY_6000_5_OR_NEWER
                return ulong.TryParse(
                    text, NumberStyles.Integer, CultureInfo.InvariantCulture, out raw);
#else
                if (long.TryParse(
                    text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) &&
                    id >= int.MinValue && id <= (long)uint.MaxValue)
                {
                    raw = unchecked((ulong)(uint)id);
                    return true;
                }
                return false;
#endif
            }
            return false;
        }

        /// <summary>The live session key of <paramref name="obj"/>.</summary>
        internal static ulong RawOf(UnityEngine.Object obj)
        {
#if UNITY_6000_5_OR_NEWER
            return EntityId.ToULong(obj.GetEntityId());
#else
#pragma warning disable CS0618 // InstanceID is the SPEC handle below 6000.5; warning-obsolete on 6000.3–6000.4, compile error only on 6000.5+ (L-04).
            return unchecked((ulong)(uint)obj.GetInstanceID());
#pragma warning restore CS0618
#endif
        }

        /// <summary>
        /// Resolves a session key to the live object, or
        /// <see langword="null"/> when no live object carries it — the only
        /// failure signal (L-04).
        /// </summary>
        internal static UnityEngine.Object? Resolve(ulong raw)
        {
#if UNITY_6000_5_OR_NEWER
            return EditorUtility.EntityIdToObject(EntityId.FromULong(raw));
#else
#pragma warning disable CS0618
            return EditorUtility.InstanceIDToObject(unchecked((int)(uint)raw));
#pragma warning restore CS0618
#endif
        }

        /// <summary>
        /// The JSON form of <paramref name="obj"/>'s session key: below
        /// 6000.5 the signed InstanceID, which always fits the
        /// Canonical-JSON Int64 range (L-04 (b)); on 6000.5+ the EntityId's
        /// raw ulong as an integer when it fits, otherwise the
        /// decimal-digit string the Codec also accepts (L-04 note 3).
        /// </summary>
        internal static JToken Write(UnityEngine.Object obj)
        {
#if UNITY_6000_5_OR_NEWER
            var raw = RawOf(obj);
            return raw <= (ulong)long.MaxValue
                ? new JValue((long)raw)
                : new JValue(raw.ToString(CultureInfo.InvariantCulture));
#else
#pragma warning disable CS0618 // InstanceID is the SPEC handle below 6000.5 (L-04).
            return new JValue((long)obj.GetInstanceID());
#pragma warning restore CS0618
#endif
        }
    }
}
