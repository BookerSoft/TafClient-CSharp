// TafToolbox.Crc32 – C# port of nswf/nswfl_crc32.h / .cpp
// Standard CRC-32 (ISO 3309 / ITU-T V.42 polynomial 0xEDB88320).
// The original C++ used the NSWFL::Hashing::CRC32 class.
// This implementation is a direct drop-in replacement that produces
// identical output.

namespace TafToolbox.Crc32
{
    /// <summary>
    /// Standard CRC-32 calculator.
    /// Matches the output of NSWFL::Hashing::CRC32::FullCRC from the C++ toolbox.
    /// </summary>
    public static class Crc32Calculator
    {
        // Pre-computed lookup table for the CRC-32 polynomial 0xEDB88320
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            const uint Polynomial = 0xEDB88320u;
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int j = 0; j < 8; j++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ Polynomial : crc >> 1;
                table[i] = crc;
            }
            return table;
        }

        /// <summary>
        /// Computes the CRC-32 of the given byte array.
        /// The result is equivalent to NSWFL::Hashing::CRC32::FullCRC().
        /// </summary>
        public static uint Compute(byte[] data) => Compute(data, 0, data.Length);

        /// <summary>
        /// Computes the CRC-32 over <paramref name="length"/> bytes of
        /// <paramref name="data"/> starting at <paramref name="offset"/>.
        /// </summary>
        public static uint Compute(byte[] data, int offset, int length)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = offset; i < offset + length; i++)
                crc = (crc >> 8) ^ Table[(crc ^ data[i]) & 0xFF];
            return ~crc;
        }

        /// <summary>
        /// Returns the CRC-32 as a lower-case hex string, byte-reversed
        /// (matching the C++ toolbox's QByteArray::toHex() output).
        /// </summary>
        public static string ToHexString(uint crc)
        {
            // The C++ code reverses the 4 bytes of the CRC before calling toHex(),
            // which means byte[0] is the LSB of the uint.
            byte b0 = (byte)( crc        & 0xFF);
            byte b1 = (byte)((crc >>  8) & 0xFF);
            byte b2 = (byte)((crc >> 16) & 0xFF);
            byte b3 = (byte)((crc >> 24) & 0xFF);
            return $"{b3:x2}{b2:x2}{b1:x2}{b0:x2}";
        }

        /// <summary>
        /// Computes CRC-32 and returns it as the byte-reversed hex string.
        /// Convenience wrapper used by CompareAssets.
        /// </summary>
        public static string ComputeHex(byte[] data) => ToHexString(Compute(data));
    }
}
