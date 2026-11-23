#nullable enable

using System;
using System.Globalization;

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// CRC-32 (ISO-HDLC / zlib polynomial) of SPEC §0.6 SG-12: init
    /// 0xFFFFFFFF, reflected polynomial 0xEDB88320, final XOR 0xFFFFFFFF,
    /// rendered as 8 lowercase hex digits. It is a corruption checksum for
    /// journal lines, not a signature.
    /// </summary>
    internal static class Crc32
    {
        private const uint Polynomial = 0xEDB88320u;
        private const uint InitialValue = 0xFFFFFFFFu;
        private const uint FinalXor = 0xFFFFFFFFu;

        private static readonly uint[] s_table = BuildTable();

        /// <summary>The CRC-32/ISO-HDLC value of <paramref name="bytes"/>.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="bytes"/> is null.</exception>
        public static uint Compute(byte[] bytes)
        {
            if (bytes is null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }
            var crc = InitialValue;
            for (var i = 0; i < bytes.Length; i++)
            {
                crc = s_table[(crc ^ bytes[i]) & 0xFF] ^ (crc >> 8);
            }
            return crc ^ FinalXor;
        }

        /// <summary>The 8-digit lowercase-hex form of a CRC-32 value (SPEC §0.6 SG-12).</summary>
        public static string ToLowerHex(uint crc) => crc.ToString("x8", CultureInfo.InvariantCulture);

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                var crc = i;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ Polynomial : crc >> 1;
                }
                table[i] = crc;
            }
            return table;
        }
    }
}
