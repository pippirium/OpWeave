#nullable enable

// T-W2.7-03 fixture: the ScriptableObject type the
// unity.asset.create_scriptable_object tests create and reconcile. Unity
// resolves an asset's script by a file of the same name as its type, so the
// type owns this file; the sibling classes it needs (an enum and a
// [Serializable] member type) live beside it.
using System;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// Fixture asset whose serialized fields exercise every value kind the
    /// <c>values</c> port accepts: integer (including a bounded
    /// <see cref="byte"/>), boolean, float, string, enum by member name,
    /// color, vector, object reference, a nested serializable member object
    /// and an array.
    /// </summary>
    public sealed class ProbeFieldsAsset : ScriptableObject
    {
        /// <summary>The enum kind a name-string value sets.</summary>
        public enum ProbeKind
        {
            Alpha,
            Beta,
            Gamma,
        }

        /// <summary>The nested serializable member of the Generic branch.</summary>
        [Serializable]
        public sealed class ProbeNested
        {
            public int number;
            public string text = string.Empty;
        }

        public int marker;
        public byte small;
        public string label = string.Empty;
        public bool enabled;
        public float scalar;
        public Color tint;
        public Vector3 offset;
        public ProbeKind kind;
        public Texture2D? surface;
        public ProbeNested nested = new ProbeNested();
        public int[] numbers = new int[0];
        // A user-defined serialized field whose name collides with one of
        // Unity's hierarchy-link properties: the content comparison must
        // still see it (F3 regression fixture).
        public int m_Father;
    }
}
