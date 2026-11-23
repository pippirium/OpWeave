#nullable enable

using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The Ref resolver of SPEC §G2: turns a Ref JSON value into the Unity
    /// entity it identifies, trying the Ref's keys in the SPEC resolution
    /// order and reporting conflicts between keys as OW-UNITY-002. Resolving
    /// never caches — every call evaluates the Ref's keys against the live
    /// Editor state again.
    /// </summary>
    /// <remarks>
    /// <see cref="ToRef"/> performs the inverse direction: it produces a Ref
    /// whose keys are all filled with the values the live object carries —
    /// every persistent key (guid, path, globalId) and the session key
    /// (<c>handle</c>) the object can hold (SPEC §G2 "出力する Ref には持てる
    /// キーをすべて埋める").
    /// </remarks>
    public interface IRefResolver
    {
        /// <summary>
        /// Resolves <paramref name="refJson"/> to the Unity object it
        /// identifies, applying the SPEC §G2 resolution order of the Ref's
        /// <c>kind</c> discriminator. Failures are OW-UNITY-001 (no key finds
        /// an object), OW-UNITY-002 (ambiguous path or keys that contradict
        /// the resolved entity), OW-UNITY-003 (the scene the Ref selects is
        /// not loaded, including an unopened <c>prefab-contents:</c> resource)
        /// and OW-UNITY-004 (asset/instance mix-up); a malformed Ref shape is
        /// OW-TYPE-003.
        /// </summary>
        Result<T> Resolve<T>(JsonValue refJson) where T : UnityEngine.Object;

        /// <summary>
        /// Produces the Ref of type <paramref name="refTypeId"/> for
        /// <paramref name="obj"/> with every key the object can hold filled
        /// (SPEC §G2). <paramref name="refTypeId"/> is a SPEC §B5 Ref Type ID
        /// such as <c>unity.GameObjectRef</c> or
        /// <c>unity.ComponentRef&lt;unity.Animator&gt;</c>; an incompatible
        /// (object, refTypeId) pair is a programming error
        /// (<see cref="System.ArgumentException"/>).
        /// </summary>
        JsonValue ToRef(UnityEngine.Object obj, string refTypeId);
    }
}
