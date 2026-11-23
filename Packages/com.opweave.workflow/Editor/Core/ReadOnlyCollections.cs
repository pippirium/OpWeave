#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OpWeave.Core
{
    /// <summary>
    /// Defensive-copy helpers for the read-only collections held by Core public models
    /// (SPEC §0.4). Lists are copied to arrays; dictionaries use Ordinal key comparison.
    /// The returned wrappers cannot be mutated and never observe later changes to the source.
    /// </summary>
    public static class ReadOnlyCollections
    {
        /// <summary>Copies <paramref name="source"/> into an array-backed read-only list.</summary>
        public static IReadOnlyList<T> CopyList<T>(IEnumerable<T> source)
        {
            if (source is null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            return Array.AsReadOnly(new List<T>(source).ToArray());
        }

        /// <summary>
        /// Copies <paramref name="source"/> into an Ordinal-keyed read-only dictionary.
        /// Null or duplicate keys are rejected.
        /// </summary>
        public static IReadOnlyDictionary<string, TValue> CopyMap<TValue>(
            IEnumerable<KeyValuePair<string, TValue>> source)
        {
            if (source is null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            var map = new Dictionary<string, TValue>(StringComparer.Ordinal);
            foreach (var pair in source)
            {
                if (pair.Key is null)
                {
                    throw new ArgumentException(
                        "Dictionary keys must not be null.", nameof(source));
                }
                map.Add(pair.Key, pair.Value);
            }
            return new ReadOnlyDictionary<string, TValue>(map);
        }
    }
}
