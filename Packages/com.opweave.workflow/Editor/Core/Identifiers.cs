#nullable enable

using System;
using System.Security.Cryptography;

namespace OpWeave.Core
{
    /// <summary>
    /// Identifier generation (SPEC §0.4, §D7): runId is <c>"run_" + ULID</c> and PlanId is
    /// <c>"plan_" + ULID</c>. ULIDs are 26 Crockford Base32 characters encoding a 48-bit
    /// millisecond timestamp and 80 bits of randomness; identifiers created by this generator
    /// are monotonic and sort by creation time.
    /// </summary>
    public static class Identifiers
    {
        public const string RunIdPrefix = "run_";
        public const string PlanIdPrefix = "plan_";

        // Crockford Base32 (no I, L, O, U).
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        private const long TimestampMask = (1L << 48) - 1;

        // Core is called from the main thread only and holds no thread locks (SPEC §0.4).
        private static readonly RandomNumberGenerator s_rng = RandomNumberGenerator.Create();
        private static long s_lastTimestamp = -1;
        private static readonly byte[] s_lastRandom = new byte[10];

        /// <summary>A new Run identifier: "run_" + ULID (SPEC §0.4).</summary>
        public static string NewRunId() => RunIdPrefix + NewUlid();

        /// <summary>A new Plan identifier: "plan_" + ULID (SPEC §D7).</summary>
        public static string NewPlanId() => PlanIdPrefix + NewUlid();

        /// <summary>A new monotonic ULID of 26 Crockford Base32 characters.</summary>
        public static string NewUlid()
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() & TimestampMask;
            if (timestamp > s_lastTimestamp)
            {
                s_lastTimestamp = timestamp;
                s_rng.GetBytes(s_lastRandom);
            }
            else
            {
                // Same millisecond or a backwards clock: keep the last timestamp and
                // increment the random component so IDs stay ordered by creation time.
                IncrementRandom();
            }
            return Encode(s_lastTimestamp, s_lastRandom);
        }

        private static void IncrementRandom()
        {
            for (var i = s_lastRandom.Length - 1; i >= 0; i--)
            {
                if (++s_lastRandom[i] != 0)
                {
                    return;
                }
            }
            // 2^80 identifiers inside one millisecond cannot occur; keep the ordering
            // guarantee anyway by advancing the stored timestamp artificially.
            s_lastTimestamp++;
            s_rng.GetBytes(s_lastRandom);
        }

        private static string Encode(long timestamp, byte[] random)
        {
            var chars = new char[26];
            chars[0] = Alphabet[(int)((timestamp >> 45) & 0x1F)];
            chars[1] = Alphabet[(int)((timestamp >> 40) & 0x1F)];
            chars[2] = Alphabet[(int)((timestamp >> 35) & 0x1F)];
            chars[3] = Alphabet[(int)((timestamp >> 30) & 0x1F)];
            chars[4] = Alphabet[(int)((timestamp >> 25) & 0x1F)];
            chars[5] = Alphabet[(int)((timestamp >> 20) & 0x1F)];
            chars[6] = Alphabet[(int)((timestamp >> 15) & 0x1F)];
            chars[7] = Alphabet[(int)((timestamp >> 10) & 0x1F)];
            chars[8] = Alphabet[(int)((timestamp >> 5) & 0x1F)];
            chars[9] = Alphabet[(int)(timestamp & 0x1F)];
            chars[10] = Alphabet[random[0] >> 3];
            chars[11] = Alphabet[((random[0] << 2) | (random[1] >> 6)) & 0x1F];
            chars[12] = Alphabet[(random[1] >> 1) & 0x1F];
            chars[13] = Alphabet[((random[1] << 4) | (random[2] >> 4)) & 0x1F];
            chars[14] = Alphabet[((random[2] << 1) | (random[3] >> 7)) & 0x1F];
            chars[15] = Alphabet[(random[3] >> 2) & 0x1F];
            chars[16] = Alphabet[((random[3] << 3) | (random[4] >> 5)) & 0x1F];
            chars[17] = Alphabet[random[4] & 0x1F];
            chars[18] = Alphabet[random[5] >> 3];
            chars[19] = Alphabet[((random[5] << 2) | (random[6] >> 6)) & 0x1F];
            chars[20] = Alphabet[(random[6] >> 1) & 0x1F];
            chars[21] = Alphabet[((random[6] << 4) | (random[7] >> 4)) & 0x1F];
            chars[22] = Alphabet[((random[7] << 1) | (random[8] >> 7)) & 0x1F];
            chars[23] = Alphabet[(random[8] >> 2) & 0x1F];
            chars[24] = Alphabet[((random[8] << 3) | (random[9] >> 5)) & 0x1F];
            chars[25] = Alphabet[random[9] & 0x1F];
            return new string(chars);
        }
    }
}
