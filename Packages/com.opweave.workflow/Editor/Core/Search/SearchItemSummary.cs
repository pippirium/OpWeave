#nullable enable

// T-W3.2-02: the SPEC §H3 "search の 1 件の上限" item builder lives in
// OpWeave.Core — the §C6 compact summary of one search_operations
// result, always at most 600 UTF-8 bytes of Canonical JSON measured on
// the final wire form including the "truncated" marker. AgentApi emits
// this builder's output unchanged and the dotnet Core test project
// compiles the same source, so the byte bound and the reduction order
// have exactly one implementation.

using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core.Search
{
    /// <summary>
    /// Builds one <c>search_operations</c> item — the SPEC §C6 compact
    /// summary — inside the SPEC §H3 bound of 600 UTF-8 bytes of
    /// Canonical JSON, measured on the item's final form including the
    /// <c>truncated</c> marker.
    /// </summary>
    /// <remarks>
    /// An item over the bound is reduced in the SPEC §H3 order, stopping
    /// at the first step that makes it fit:
    /// <list type="number">
    /// <item>item-level <c>"truncated": true</c> is added — an unreduced
    /// item never carries it;</item>
    /// <item><c>summary</c> is trimmed from the end one UTF-16 code unit
    /// at a time, never splitting a surrogate pair, down to
    /// <c>""</c>;</item>
    /// <item><c>displayName</c> is trimmed the same way;</item>
    /// <item><c>outputs</c> entries drop from the last declared
    /// port;</item>
    /// <item><c>inputs</c> entries drop the same way;</item>
    /// <item><c>effects</c> elements drop from the end of the
    /// array.</item>
    /// </list>
    /// The identity members — <c>id</c>, <c>version</c>, <c>origin</c>,
    /// <c>destructive</c>, <c>mode</c> — are never reduced (SPEC §C6
    /// item 6): the §A4 grammar bounds an Operation ID at 128 characters,
    /// so they always fit. Entries drop whole — a kept port name or Type
    /// ID is never a fragment — and every field shape (string, object,
    /// array) stays as SPEC §C6 declares. The same
    /// <see cref="OperationSearchResult"/> always builds the same bytes.
    /// </remarks>
    public static class SearchItemSummary
    {
        /// <summary>
        /// The SPEC §H3 bound on one search summary item: 600 UTF-8 bytes
        /// of Canonical JSON, measured on the item's own wire object.
        /// </summary>
        public const int MaxBytes = 600;

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false, true);

        /// <summary>
        /// The item's §C6 wire object reduced to the §H3 bound. A result
        /// whose natural size fits returns the unreduced object — with no
        /// <c>truncated</c> member.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="result"/> is null.
        /// </exception>
        public static JObject Build(OperationSearchResult result)
        {
            if (result is null)
            {
                throw new ArgumentNullException(nameof(result));
            }
            var descriptor = result.Operation.Descriptor;
            var item = new JObject
            {
                ["id"] = descriptor.Id,
                ["version"] = descriptor.Version,
                ["origin"] = JsonNaming.ToSnakeCase(result.Operation.Origin.ToString()),
                ["displayName"] = result.DisplayName,
                ["summary"] = result.Summary,
                ["inputs"] = PortMapJson(result.Inputs),
                ["outputs"] = PortMapJson(result.Outputs),
                ["effects"] = new JArray(descriptor.SideEffects),
                ["destructive"] = descriptor.Destructive,
                ["mode"] = JsonNaming.ToSnakeCase(descriptor.ExecutionMode.ToString()),
            };
            if (Fits(item))
            {
                return item;
            }
            // The §H3 reduction order: a step runs only while the item
            // stays over the bound and the build stops at the first fit —
            // the size is always measured on the current wire object,
            // marker included.
            item["truncated"] = true;
            if (Fits(item))
            {
                return item;
            }
            TrimEnd(item, "summary");
            if (Fits(item))
            {
                return item;
            }
            TrimEnd(item, "displayName");
            if (Fits(item))
            {
                return item;
            }
            DropLast(item, "outputs");
            if (Fits(item))
            {
                return item;
            }
            DropLast(item, "inputs");
            if (Fits(item))
            {
                return item;
            }
            DropLast(item, "effects");
            return item;
        }

        // Whether the item's Canonical JSON stays inside the §H3 bound —
        // measured on the exact wire object, marker included.
        private static bool Fits(JObject item) =>
            s_utf8.GetByteCount(CanonicalJson.Serialize(item)) <= MaxBytes;

        // Trims one free-text member one UTF-16 code unit at a time while
        // the item stays over the bound: a display text is cut at a
        // character boundary — never mid-surrogate-pair — down to the
        // empty string.
        private static void TrimEnd(JObject item, string member)
        {
            var text = (string)item[member]!;
            while (text.Length > 0 && !Fits(item))
            {
                var cut = text.Length - 1;
                if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
                {
                    // Never leave half a surrogate pair behind.
                    cut--;
                }
                text = text.Substring(0, cut);
                item[member] = text;
            }
        }

        // Drops the last entry of a port map or the last element of the
        // effect list while the item stays over the bound — whole entries
        // only, so a kept port name or Type ID is never a fragment and
        // the survivors keep declaration order.
        private static void DropLast(JObject item, string member)
        {
            if (item[member] is JObject map)
            {
                while (map.Count > 0 && !Fits(item))
                {
                    JProperty? last = null;
                    foreach (var property in map.Properties())
                    {
                        last = property;
                    }
                    last?.Remove();
                }
            }
            else if (item[member] is JArray array)
            {
                while (array.Count > 0 && !Fits(item))
                {
                    array.RemoveAt(array.Count - 1);
                }
            }
        }

        private static JObject PortMapJson(IReadOnlyDictionary<string, string> ports)
        {
            var map = new JObject();
            foreach (var pair in ports)
            {
                map[pair.Key] = pair.Value;
            }
            return map;
        }
    }
}
