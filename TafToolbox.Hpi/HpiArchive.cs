// TafToolbox.Hpi – C# port of rwe/hpi/HpiArchive.cpp (gpgnet4ta-develop/libs/rwe/hpi)
// Reads Total Annihilation HPI / UFO / GPF / CCX archive files.
//
// This is a byte-for-byte faithful port of the authoritative C++ source.
// Every offset, key-derivation step, and decompression algorithm below
// matches HpiArchive.cpp / hpi_util.cpp / hpi_headers.h exactly.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TafToolbox.Hpi
{
    public sealed class HpiException : Exception
    {
        public HpiException(string message) : base(message) { }
    }

    /// <summary>
    /// Reads a Total Annihilation HPI archive from a stream.
    /// Equivalent to rwe::HpiArchive in the C++ toolbox.
    /// </summary>
    public sealed class HpiArchive : IDisposable
    {
        // ── Constants (hpi_headers.h) ────────────────────────────────────────
        private const uint HpiMagicNumber      = 0x49504148; // "HAPI"
        private const uint HpiVersionNumber    = 0x00010000;
        private const uint HpiChunkMagicNumber = 0x48535153; // "SQSH"

        // ── Public model (mirrors HpiArchive::File / Directory / DirectoryEntry) ─
        public enum CompressionScheme { None = 0, LZ77 = 1, ZLib = 2 }

        public sealed class FileEntry
        {
            public CompressionScheme CompressionScheme { get; }
            public int Offset { get; }
            public int Size   { get; }

            internal FileEntry(CompressionScheme scheme, int offset, int size)
            {
                CompressionScheme = scheme;
                Offset = offset;
                Size   = size;
            }
        }

        public sealed class DirectoryEntry
        {
            public IReadOnlyList<Entry> Entries { get; }
            internal DirectoryEntry(List<Entry> entries) => Entries = entries;
        }

        public sealed class Entry
        {
            public string          Name      { get; }
            public FileEntry?      File      { get; }
            public DirectoryEntry? Directory { get; }

            internal Entry(string name, FileEntry file)     { Name = name; File = file; }
            internal Entry(string name, DirectoryEntry dir) { Name = name; Directory = dir; }
        }

        // ── Private state ─────────────────────────────────────────────────────
        private readonly Stream _stream;
        private readonly byte   _decryptionKey;
        private readonly DirectoryEntry _root;

        // ── Constructor — mirrors HpiArchive::HpiArchive(istream*) exactly ─────
        public HpiArchive(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));

            // HpiVersion: { uint32 marker; uint32 version; } — 8 bytes
            uint marker  = ReadUInt32(stream);
            uint version = ReadUInt32(stream);
            if (marker != HpiMagicNumber)
                throw new HpiException("Invalid HPI file marker");
            if (version != HpiVersionNumber)
                throw new HpiException("Unsupported HPI version (saved-game BANK files not supported)");

            // HpiHeader: { uint32 directorySize; uint32 headerKey; uint32 start; } — 12 bytes
            uint directorySize = ReadUInt32(stream);
            uint headerKey     = ReadUInt32(stream);
            uint start         = ReadUInt32(stream);

            _decryptionKey = TransformKey((byte)headerKey);

            // Allocate a buffer the size of the WHOLE directory region (directorySize bytes).
            // Bytes [0, start) are left uninitialized — they are never read by the directory
            // parser since all real content starts at byte offset 'start'.
            // Mirrors: auto data = make_unique<char[]>(h.directorySize);
            //          readAndDecrypt(stream, key, data.get() + h.start, h.directorySize - h.start);
            stream.Seek(start, SeekOrigin.Begin);
            byte[] data = new byte[directorySize];
            int toRead = (int)(directorySize - start);
            ReadAndDecrypt(stream, _decryptionKey, data, (int)start, toRead);

            if (start + 8 > directorySize) // sizeof(HpiDirectoryData) = 8
                throw new HpiException("Runaway root directory");

            // Root HpiDirectoryData lives at byte offset 'start' within the buffer.
            _root = ConvertDirectory(data, (int)start, (int)directorySize);
        }

        // ── Public API ────────────────────────────────────────────────────────

        public DirectoryEntry Root => _root;

        /// <summary>Extracts the contents of <paramref name="file"/> into <paramref name="buffer"/>.</summary>
        public void Extract(FileEntry file, byte[] buffer)
        {
            if (buffer.Length < file.Size)
                throw new ArgumentException("Buffer is too small", nameof(buffer));

            _stream.Seek(file.Offset, SeekOrigin.Begin);

            switch (file.CompressionScheme)
            {
                case CompressionScheme.None:
                    ReadAndDecrypt(_stream, _decryptionKey, buffer, 0, file.Size);
                    break;
                case CompressionScheme.LZ77:
                case CompressionScheme.ZLib:
                    ExtractCompressed(_stream, _decryptionKey, buffer, file.Size);
                    break;
                default:
                    throw new HpiException("Invalid file entry compression scheme");
            }
        }

        public FileEntry? FindFile(string path)
        {
            string[] parts = path.Split('/');
            DirectoryEntry dir = _root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var next = FindChildDirectory(dir, parts[i]);
                if (next is null) return null;
                dir = next;
            }
            foreach (var e in dir.Entries)
                if (e.File != null && string.Equals(e.Name, parts[^1], StringComparison.OrdinalIgnoreCase))
                    return e.File;
            return null;
        }

        public DirectoryEntry? FindDirectory(string path)
        {
            string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            DirectoryEntry dir = _root;
            foreach (var part in parts)
            {
                var next = FindChildDirectory(dir, part);
                if (next is null) return null;
                dir = next;
            }
            return dir;
        }

        private static DirectoryEntry? FindChildDirectory(DirectoryEntry dir, string name)
        {
            foreach (var e in dir.Entries)
                if (e.Directory != null && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                    return e.Directory;
            return null;
        }

        public List<string> FindRootDirectoriesWithPrefix(string prefix)
        {
            var matches = new List<string>();
            foreach (var entry in _root.Entries)
            {
                if (entry.Directory is null) continue;
                if (entry.Name.Length >= prefix.Length &&
                    entry.Name.Substring(0, prefix.Length).Equals(prefix, StringComparison.OrdinalIgnoreCase))
                    matches.Add(entry.Name);
            }
            return matches;
        }

        // ── Directory tree conversion (mirrors convertDirectory / convertDirectoryEntry) ─

        private static DirectoryEntry ConvertDirectory(byte[] buffer, int offset, int size)
        {
            // HpiDirectoryData: { uint32 numberOfEntries; uint32 entryListOffset; }
            uint numberOfEntries = BitConverter.ToUInt32(buffer, offset);
            uint entryListOffset = BitConverter.ToUInt32(buffer, offset + 4);

            const int entrySize = 9; // HpiDirectoryEntry: uint32+uint32+uint8 = 9 bytes, packed
            if (entryListOffset + (ulong)numberOfEntries * entrySize > (uint)size)
                throw new HpiException("Runaway directory entry list");

            var entries = new List<Entry>((int)numberOfEntries);
            int p = (int)entryListOffset;
            for (int i = 0; i < numberOfEntries; i++)
            {
                entries.Add(ConvertDirectoryEntry(buffer, p, size));
                p += entrySize;
            }
            return new DirectoryEntry(entries);
        }

        private static Entry ConvertDirectoryEntry(byte[] buffer, int entryOffset, int size)
        {
            // HpiDirectoryEntry: { uint32 nameOffset; uint32 dataOffset; uint8 isDirectory; }
            uint nameOffset  = BitConverter.ToUInt32(buffer, entryOffset);
            uint dataOffset  = BitConverter.ToUInt32(buffer, entryOffset + 4);
            byte isDirectory = buffer[entryOffset + 8];

            int nameLen = StringSize(buffer, (int)nameOffset, size);
            if (nameLen < 0) throw new HpiException("Runaway directory entry name");
            string name = Encoding.ASCII.GetString(buffer, (int)nameOffset, nameLen);

            // CRITICAL: isDirectory != 0 → directory, == 0 → file (per hpi_headers.h)
            if (isDirectory != 0)
            {
                if (dataOffset + 8 > (uint)size) // sizeof(HpiDirectoryData) = 8
                    throw new HpiException("Runaway directory data offset");
                var subdir = ConvertDirectory(buffer, (int)dataOffset, size);
                return new Entry(name, subdir);
            }
            else
            {
                if (dataOffset + 9 > (uint)size) // sizeof(HpiFileData) = 4+4+1 = 9
                    throw new HpiException("Runaway file data offset");
                int fileDataOffset   = (int)dataOffset;
                int dOffset          = BitConverter.ToInt32(buffer, fileDataOffset);
                int fileSize         = BitConverter.ToInt32(buffer, fileDataOffset + 4);
                byte compressionByte = buffer[fileDataOffset + 8];
                var file = new FileEntry((CompressionScheme)compressionByte, dOffset, fileSize);
                return new Entry(name, file);
            }
        }

        private static int StringSize(byte[] buffer, int start, int end)
        {
            for (int i = start; i < end; i++)
                if (buffer[i] == 0) return i - start;
            return -1; // runaway — no null terminator found before 'end'
        }

        // ── Key derivation & decryption (mirrors hpi_util.cpp exactly) ──────────

        /// <summary>transformKey(key) = (key &lt;&lt; 2) | (key &gt;&gt; 6), as a single byte.</summary>
        private static byte TransformKey(byte key) => (byte)((key << 2) | (key >> 6));

        /// <summary>
        /// decrypt: buf[i] = (seed+i) XOR key XOR buf[i], where seed is the stream
        /// position of the FIRST byte being decrypted, truncated to a byte.
        /// No-op if key == 0 (unencrypted archive).
        /// </summary>
        private static void Decrypt(byte key, byte seed, byte[] buf, int offset, int count)
        {
            if (key == 0) return;
            for (int i = 0; i < count; i++)
            {
                byte pos = (byte)(seed + (byte)i);
                buf[offset + i] = (byte)((pos ^ key) ^ buf[offset + i]);
            }
        }

        /// <summary>
        /// Reads <paramref name="count"/> bytes from the stream into buf[offset..],
        /// then decrypts them in place. The seed is the stream position BEFORE the read.
        /// </summary>
        private static void ReadAndDecrypt(Stream stream, byte key, byte[] buf, int offset, int count)
        {
            byte seed = (byte)stream.Position;
            int totalRead = 0;
            while (totalRead < count)
            {
                int n = stream.Read(buf, offset + totalRead, count - totalRead);
                if (n == 0) break; // EOF
                totalRead += n;
            }
            Decrypt(key, seed, buf, offset, totalRead);
        }

        private static uint ReadUInt32(Stream stream)
        {
            byte[] b = new byte[4];
            int n = stream.Read(b, 0, 4);
            if (n != 4) throw new HpiException("Unexpected end of stream reading header");
            return BitConverter.ToUInt32(b, 0);
        }

        // ── Inner decryption for encrypted chunks (decryptInner) ────────────────

        /// <summary>buffer[i] = (buffer[i] - i) XOR i — position-only, no archive key involved.</summary>
        private static void DecryptInner(byte[] buffer, int count)
        {
            for (int i = 0; i < count; i++)
            {
                byte pos = (byte)i;
                buffer[i] = (byte)((byte)(buffer[i] - pos) ^ pos);
            }
        }

        private static uint ComputeChecksum(byte[] buffer, int count)
        {
            uint sum = 0;
            for (int i = 0; i < count; i++) sum += buffer[i];
            return sum;
        }

        // ── Chunked (compressed) file extraction (mirrors extractCompressed) ────

        private static void ExtractCompressed(Stream stream, byte key, byte[] buffer, int size)
        {
            int chunkCount = (size / 65536) + (size % 65536 == 0 ? 0 : 1);

            // Read+decrypt the chunk-size table as ONE block (uint32 per chunk).
            // Sizes aren't strictly needed afterward since each chunk header carries its own.
            byte[] chunkSizesRaw = new byte[chunkCount * 4];
            ReadAndDecrypt(stream, key, chunkSizesRaw, 0, chunkSizesRaw.Length);

            int bufferOffset = 0;
            for (int i = 0; i < chunkCount; i++)
            {
                // HpiChunk header (19 bytes, no padding): uint32 marker, u8 version,
                //   u8 compressionScheme, u8 encrypted, uint32 compressedSize,
                //   uint32 decompressedSize, uint32 checksum
                // Verified against real archive bytes: checksum only matches when this
                // header is read as exactly 19 bytes (4+1+1+1+4+4+4), not 20.
                byte[] hdr = new byte[19];
                ReadAndDecrypt(stream, key, hdr, 0, 19);

                uint chunkMarker       = BitConverter.ToUInt32(hdr, 0);
                byte compressionScheme = hdr[5];
                byte encrypted         = hdr[6];
                uint compressedSize    = BitConverter.ToUInt32(hdr, 7);
                uint decompressedSize  = BitConverter.ToUInt32(hdr, 11);
                uint checksum          = BitConverter.ToUInt32(hdr, 15);

                if (chunkMarker != HpiChunkMagicNumber)
                    throw new HpiException("Invalid chunk header");

                if (bufferOffset + decompressedSize > size)
                    throw new HpiException("Extracted file larger than expected");

                byte[] chunkBuffer = new byte[compressedSize];
                ReadAndDecrypt(stream, key, chunkBuffer, 0, (int)compressedSize);

                uint actualChecksum = ComputeChecksum(chunkBuffer, (int)compressedSize);
                if (actualChecksum != checksum)
                    throw new HpiException("Invalid chunk checksum");

                if (encrypted != 0)
                    DecryptInner(chunkBuffer, (int)compressedSize);

                switch (compressionScheme)
                {
                    case 0: // no compression
                        if (compressedSize != decompressedSize)
                            throw new HpiException("Uncompressed chunk has different decompressed and compressed sizes");
                        Array.Copy(chunkBuffer, 0, buffer, bufferOffset, (int)compressedSize);
                        bufferOffset += (int)decompressedSize;
                        break;

                    case 1: // LZ77
                        DecompressLz77(chunkBuffer, (int)compressedSize, buffer, bufferOffset, (int)decompressedSize);
                        bufferOffset += (int)decompressedSize;
                        break;

                    case 2: // ZLib
                        DecompressZLib(chunkBuffer, (int)compressedSize, buffer, bufferOffset, (int)decompressedSize);
                        bufferOffset += (int)decompressedSize;
                        break;

                    default:
                        throw new HpiException("Invalid compression scheme");
                }
            }
        }

        // ── LZ77 decompression (mirrors decompressLZ77 exactly — sliding window) ─

        private static void DecompressLz77(byte[] input, int inLen, byte[] output, int outOffset, int maxBytes)
        {
            byte[] window = new byte[4096];
            int  inPos  = 0;
            int  outPos = 0;
            uint windowPos = 1;

            while (true)
            {
                if (inPos >= inLen)
                    throw new HpiException("LZ77 decompress expected tag but got end of input");

                byte tag = input[inPos++];

                for (int bit = 0; bit < 8; bit++)
                {
                    if ((tag & 1) == 0) // literal byte
                    {
                        if (inPos >= inLen)
                            throw new HpiException("LZ77 decompress expected byte but got end of input");
                        if (outPos >= maxBytes)
                            throw new HpiException("LZ77 decompress ran over max output bytes");

                        byte b = input[inPos];
                        output[outOffset + outPos++] = b;
                        window[windowPos] = b;
                        windowPos = (windowPos + 1) & 0xFFF;
                        inPos++;
                    }
                    else // back-reference into sliding window
                    {
                        if (inPos >= inLen - 1)
                            throw new HpiException("LZ77 decompress expected window offset/length but got end of input");

                        uint packedData = (uint)(input[inPos] | (input[inPos + 1] << 8));
                        inPos += 2;

                        uint offset = packedData >> 4;
                        if (offset == 0)
                            return; // end-of-stream sentinel

                        uint count = (packedData & 0x0F) + 2;
                        if (outPos + count > maxBytes)
                            throw new HpiException("LZ77 decompress ran over max output bytes");

                        for (uint x = 0; x < count; x++)
                        {
                            byte b = window[offset];
                            output[outOffset + outPos++] = b;
                            window[windowPos] = b;
                            offset = (offset + 1) & 0xFFF;
                            windowPos = (windowPos + 1) & 0xFFF;
                        }
                    }

                    tag = (byte)(tag >> 1);
                }
            }
        }

        // ── ZLib decompression (standard zlib-wrapped deflate) ──────────────────

        private static void DecompressZLib(byte[] input, int inLen, byte[] output, int outOffset, int maxBytes)
        {
            using var ms  = new MemoryStream(input, 0, inLen);
            using var zls = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionMode.Decompress);
            int read = 0;
            byte[] tmp = new byte[maxBytes];
            while (read < maxBytes)
            {
                int n = zls.Read(tmp, read, maxBytes - read);
                if (n == 0) break;
                read += n;
            }
            Array.Copy(tmp, 0, output, outOffset, read);
        }

        public void Dispose() { /* stream owned by caller */ }
    }
}
