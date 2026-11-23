#nullable enable

// T-W2.5-04 fixture: the ScriptableObject type the unity.property.get/set
// tests create as an asset target. Unity resolves an asset's script by a
// file of the same name as its type, so the type owns this file; the
// serializable member types it nests live beside it.
using System;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// Fixture asset whose serialized fields cover every kind the
    /// <c>unity.property.*</c> Operations support — integer, boolean,
    /// float, string, enum, color, Vector2/3/4, quaternion, object
    /// reference, nested serializable members, arrays — plus an
    /// unsupported <see cref="Bounds"/> field and a member chain four
    /// levels deep for the OW-UNITY-007 boundaries.
    /// </summary>
    public sealed class PropertyProbeAsset : ScriptableObject
    {
        /// <summary>The enum kind a name-string value sets.</summary>
        public enum ProbeKind
        {
            Alpha,
            Beta,
            Gamma,
        }

        /// <summary>The depth-4 tail of "nested.inner.deep.flag".</summary>
        [Serializable]
        public sealed class ProbeDeep
        {
            public int flag;
        }

        /// <summary>The middle member of the nested chain.</summary>
        [Serializable]
        public sealed class ProbeInner
        {
            public int value;
            public ProbeDeep deep = new ProbeDeep();
        }

        /// <summary>The top-level nested serializable member.</summary>
        [Serializable]
        public sealed class ProbeNested
        {
            public int number;
            public string text = string.Empty;
            public ProbeInner inner = new ProbeInner();
        }

        public int marker;
        public byte small;
        public string label = string.Empty;
        public bool enabled;
        public float scalar;
        public Color tint;
        public Vector2 uv;
        public Vector3 offset;
        public Vector4 extents;
        public Quaternion rotation;
        public ProbeKind kind;
        public Texture2D? surface;
        public ProbeNested nested = new ProbeNested();
        public int[] numbers = new int[0];
        public ProbeNested[] nestedList = new ProbeNested[0];
        public Bounds region;
    }
}
