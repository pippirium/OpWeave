#nullable enable

using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpWeave.Core;

namespace OpWeave.Core.Tests
{
    internal sealed class ReadOnlyCollectionsTests
    {
        [Test]
        public void CopyListCopiesElementsInOrder()
        {
            var list = ReadOnlyCollections.CopyList(new[] { 3, 1, 2 });
            Assert.AreEqual(new[] { 3, 1, 2 }, list);
        }

        [Test]
        public void CopyListIsUnaffectedByLaterSourceChanges()
        {
            var source = new List<string> { "a", "b" };
            var list = ReadOnlyCollections.CopyList(source);
            source.Add("c");
            source[0] = "z";
            Assert.AreEqual(new[] { "a", "b" }, list);
        }

        [Test]
        public void CopyListWrapperRejectsMutation()
        {
            var list = ReadOnlyCollections.CopyList(new[] { 1, 2, 3 });
            Assert.IsNull(list as int[]);
            Assert.Throws<NotSupportedException>(() => ((IList<int>)list)[0] = 9);
            Assert.Throws<NotSupportedException>(() => ((IList<int>)list).Add(4));
            Assert.Throws<NotSupportedException>(() => ((ICollection<int>)list).Clear());
        }

        [Test]
        public void CopyListRejectsNullSource()
        {
            Assert.Throws<ArgumentNullException>(() => ReadOnlyCollections.CopyList<int>(null!));
        }

        [Test]
        public void CopyMapCopiesEntries()
        {
            var map = ReadOnlyCollections.CopyMap(new Dictionary<string, int>
            {
                ["b"] = 2,
                ["a"] = 1,
            });
            Assert.AreEqual(2, map.Count);
            Assert.AreEqual(1, map["a"]);
            Assert.AreEqual(2, map["b"]);
        }

        [Test]
        public void CopyMapUsesOrdinalCaseSensitiveKeys()
        {
            var map = ReadOnlyCollections.CopyMap(new Dictionary<string, int>
            {
                ["key"] = 1,
                ["KEY"] = 2,
            });
            Assert.AreEqual(2, map.Count);
            Assert.AreEqual(1, map["key"]);
            Assert.AreEqual(2, map["KEY"]);
            Assert.IsFalse(map.ContainsKey("Key"));
        }

        [Test]
        public void CopyMapIsUnaffectedByLaterSourceChanges()
        {
            var source = new Dictionary<string, int> { ["a"] = 1 };
            var map = ReadOnlyCollections.CopyMap(source);
            source["a"] = 99;
            source["b"] = 2;
            Assert.AreEqual(1, map.Count);
            Assert.AreEqual(1, map["a"]);
        }

        [Test]
        public void CopyMapWrapperRejectsMutation()
        {
            var map = ReadOnlyCollections.CopyMap(new Dictionary<string, int> { ["a"] = 1 });
            Assert.Throws<NotSupportedException>(() => ((IDictionary<string, int>)map)["b"] = 2);
            Assert.Throws<NotSupportedException>(() => ((IDictionary<string, int>)map).Remove("a"));
        }

        [Test]
        public void CopyMapRejectsDuplicateKeys()
        {
            Assert.Throws<ArgumentException>(() => ReadOnlyCollections.CopyMap(new[]
            {
                new KeyValuePair<string, int>("a", 1),
                new KeyValuePair<string, int>("a", 2),
            }));
        }

        [Test]
        public void CopyMapRejectsNullKeys()
        {
            Assert.Throws<ArgumentException>(() => ReadOnlyCollections.CopyMap(new[]
            {
                new KeyValuePair<string, int>(null!, 1),
            }));
        }

        [Test]
        public void CopyMapRejectsNullSource()
        {
            Assert.Throws<ArgumentNullException>(
                () => ReadOnlyCollections.CopyMap<int>((IEnumerable<KeyValuePair<string, int>>)null!));
        }
    }
}
