#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The HierarchyPath of SPEC §G2 (<c>/Root/Child[1]</c>): a
    /// leading-<c>/</c> chain of non-empty segments, a <c>\/</c> pair writing
    /// a literal <c>/</c> inside a GameObject name, and a trailing
    /// <c>[n]</c> suffix on a segment naming the n-th (0-based) same-name
    /// sibling at that level.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The accepted grammar is exactly the Codec's
    /// <c>unity.HierarchyPath</c> pattern: a backslash followed by
    /// <c>/</c> is one name character, a backslash followed by anything else
    /// is an ordinary character, and every other backslash or slash is a
    /// boundary. The SPEC defines no escape for a literal <c>[n]</c> suffix
    /// on a name or for a backslash directly before a slash, so those names
    /// cannot round-trip — a documented limitation of the §G2 grammar, not
    /// a resolver choice to relax.
    /// </para>
    /// </remarks>
    internal static class RefHierarchyPath
    {
        /// <summary>One parsed segment: a name and its same-name sibling index (-1 when absent).</summary>
        internal readonly struct Segment
        {
            internal Segment(string name, int index)
            {
                Name = name;
                Index = index;
            }

            internal string Name { get; }

            /// <summary>The 0-based same-name sibling index, or -1 when the segment carries no <c>[n]</c>.</summary>
            internal int Index { get; }
        }

        /// <summary>
        /// Parses <paramref name="text"/> into segments. Returns
        /// <see langword="false"/> for anything outside the Codec's
        /// <c>unity.HierarchyPath</c> grammar (an empty or non-<c>/</c>-rooted
        /// text, or an empty segment).
        /// </summary>
        internal static bool TryParse(string? text, out Segment[] segments)
        {
            segments = System.Array.Empty<Segment>();
            if (string.IsNullOrEmpty(text) || text![0] != '/')
            {
                return false;
            }
            var parsed = new List<Segment>();
            var segment = new StringBuilder(text.Length);
            var escaped = false;
            void Flush()
            {
                var name = segment.ToString();
                var index = -1;
                var open = name.LastIndexOf('[');
                if (open > 0 && name[name.Length - 1] == ']' &&
                    int.TryParse(
                        name.Substring(open + 1, name.Length - open - 2),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var suffix) &&
                    suffix >= 0)
                {
                    // A trailing "[n]" is the same-name sibling index. A
                    // "[n]" that is the whole segment stays a name — the
                    // grammar has no empty segment.
                    name = name.Substring(0, open);
                    index = suffix;
                }
                parsed.Add(new Segment(name, index));
            }
            for (var i = 1; i < text.Length; i++)
            {
                var c = text[i];
                if (escaped)
                {
                    // Only "\/" is an escape; any other backslash is an
                    // ordinary name character and the one already seen is
                    // emitted before this character.
                    if (c == '/')
                    {
                        segment.Append('/');
                    }
                    else
                    {
                        segment.Append('\\');
                        segment.Append(c);
                    }
                    escaped = false;
                    continue;
                }
                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (c == '/')
                {
                    if (segment.Length == 0)
                    {
                        return false;
                    }
                    Flush();
                    segment.Clear();
                    continue;
                }
                segment.Append(c);
            }
            if (escaped)
            {
                segment.Append('\\');
            }
            if (segment.Length == 0)
            {
                return false;
            }
            Flush();
            segments = parsed.ToArray();
            return true;
        }

        /// <summary>
        /// The HierarchyPath of <paramref name="go"/> inside its scene —
        /// the transform chain from the root, escaping <c>/</c> inside names
        /// and suffixing <c>[n]</c> wherever a level has more than one
        /// same-name sibling (SPEC §G2: the index distinguishes same-name
        /// siblings; emitting it on every member of a duplicated name keeps
        /// the written path unambiguous).
        /// </summary>
        internal static string For(GameObject go)
        {
            var chain = new List<Transform>();
            for (var t = go.transform; t is not null; t = t.parent)
            {
                chain.Add(t);
            }
            var builder = new StringBuilder();
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                var t = chain[i];
                builder.Append('/');
                builder.Append(Escape(t.name));
                var sameNameIndex = SameNameIndex(t);
                if (sameNameIndex >= 0)
                {
                    builder.Append('[');
                    builder.Append(sameNameIndex.ToString(CultureInfo.InvariantCulture));
                    builder.Append(']');
                }
            }
            return builder.ToString();
        }

        // The 0-based position of t among its same-name siblings, or -1
        // when the name is unique at its level. Roots share their level
        // across the scene's root list.
        private static int SameNameIndex(Transform t)
        {
            var position = -1;
            var count = 0;
            if (t.parent is null)
            {
                var roots = t.gameObject.scene.GetRootGameObjects();
                for (var i = 0; i < roots.Length; i++)
                {
                    if (roots[i].name == t.name)
                    {
                        if (ReferenceEquals(roots[i], t.gameObject))
                        {
                            position = count;
                        }
                        count++;
                    }
                }
            }
            else
            {
                var parent = t.parent;
                for (var i = 0; i < parent.childCount; i++)
                {
                    var sibling = parent.GetChild(i);
                    if (sibling.name == t.name)
                    {
                        if (ReferenceEquals(sibling, t))
                        {
                            position = count;
                        }
                        count++;
                    }
                }
            }
            return count > 1 ? position : -1;
        }

        private static string Escape(string name)
        {
            if (name.IndexOf('/') < 0)
            {
                return name;
            }
            return name.Replace("/", "\\/");
        }
    }
}
