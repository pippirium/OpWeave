#nullable enable

using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using OpWeave.Core;

namespace OpWeave.Core.Tests
{
    internal sealed class UtcTimestampTests
    {
        private static readonly Regex s_utcIso8601Millis = new Regex(
            @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        [Test]
        public void FormatProducesUtcIso8601WithMillisecondsAndZ()
        {
            var timestamp = new DateTimeOffset(2026, 11, 3, 4, 5, 6, 789, TimeSpan.Zero);
            Assert.AreEqual("2026-11-03T04:05:06.789Z", UtcTimestamp.Format(timestamp));
        }

        [Test]
        public void FormatConvertsNonUtcOffsetsToUtc()
        {
            var timestamp = new DateTimeOffset(2026, 11, 3, 4, 5, 6, 789, TimeSpan.FromHours(9));
            Assert.AreEqual("2026-11-02T19:05:06.789Z", UtcTimestamp.Format(timestamp));
        }

        [Test]
        public void FormatTruncatesSubMillisecondTicks()
        {
            var timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)
                .AddTicks(1239999); // 123.9999 ms
            Assert.AreEqual("2026-01-02T03:04:05.123Z", UtcTimestamp.Format(timestamp));
        }

        [Test]
        public void NowMatchesTheUtcIso8601MillisShape()
        {
            var now = UtcTimestamp.Now();
            Assert.IsTrue(s_utcIso8601Millis.IsMatch(now), $"Unexpected timestamp: {now}");
            Assert.IsTrue(now.EndsWith("Z", StringComparison.Ordinal));
        }
    }
}
