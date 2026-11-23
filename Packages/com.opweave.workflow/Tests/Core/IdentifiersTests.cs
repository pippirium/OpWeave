#nullable enable

using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpWeave.Core;

namespace OpWeave.Core.Tests
{
    internal sealed class IdentifiersTests
    {
        private const string CrockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        [Test]
        public void NewUlidIs26CrockfordBase32Characters()
        {
            var ulid = Identifiers.NewUlid();
            Assert.AreEqual(26, ulid.Length);
            foreach (var c in ulid)
            {
                Assert.IsTrue(CrockfordAlphabet.IndexOf(c) >= 0, $"Non-Crockford character '{c}'");
            }
        }

        [Test]
        public void NewUlidFirstCharacterEncodesThe128BitBound()
        {
            for (var i = 0; i < 50; i++)
            {
                var c = Identifiers.NewUlid()[0];
                Assert.IsTrue(c >= '0' && c <= '7', $"ULID out of 128-bit range: starts with '{c}'");
            }
        }

        [Test]
        public void NewUlidSortsByCreationTime()
        {
            const int count = 500;
            var previous = Identifiers.NewUlid();
            for (var i = 0; i < count; i++)
            {
                var next = Identifiers.NewUlid();
                Assert.Greater(
                    string.CompareOrdinal(next, previous), 0,
                    $"ULID #{i} does not sort after its predecessor");
                previous = next;
            }
        }

        [Test]
        public void NewUlidIsUnique()
        {
            const int count = 1000;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                Assert.IsTrue(seen.Add(Identifiers.NewUlid()), $"Duplicate ULID at #{i}");
            }
        }

        [Test]
        public void NewRunIdIsRunPrefixPlusUlid()
        {
            var runId = Identifiers.NewRunId();
            Assert.IsTrue(runId.StartsWith(Identifiers.RunIdPrefix, StringComparison.Ordinal));
            Assert.AreEqual("run_", Identifiers.RunIdPrefix);
            Assert.AreEqual(4 + 26, runId.Length);
            foreach (var c in runId.Substring(4))
            {
                Assert.IsTrue(CrockfordAlphabet.IndexOf(c) >= 0, $"Non-Crockford character '{c}'");
            }
        }

        [Test]
        public void NewPlanIdIsPlanPrefixPlusUlid()
        {
            var planId = Identifiers.NewPlanId();
            Assert.IsTrue(planId.StartsWith(Identifiers.PlanIdPrefix, StringComparison.Ordinal));
            Assert.AreEqual("plan_", Identifiers.PlanIdPrefix);
            Assert.AreEqual(5 + 26, planId.Length);
            foreach (var c in planId.Substring(5))
            {
                Assert.IsTrue(CrockfordAlphabet.IndexOf(c) >= 0, $"Non-Crockford character '{c}'");
            }
        }

        [Test]
        public void RunAndPlanIdsSortByCreationTime()
        {
            var previous = Identifiers.NewRunId();
            for (var i = 0; i < 100; i++)
            {
                var next = Identifiers.NewRunId();
                Assert.Greater(string.CompareOrdinal(next, previous), 0);
                previous = next;
            }
        }
    }
}
