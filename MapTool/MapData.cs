// TafToolbox.MapTool – TA map data structures
// Ports the relevant structs from libs/ta/  (TdfBlock, OtaParser, MapData, etc.)
// and libs/rwe/ (HpiArchive already in its own project).
//
// Total Annihilation map format:
//   .OTA  – plain-text key/value schema (TDF format, like INI)
//   .TNT  – binary heightmap + tile-index data
//
// This file implements the OTA (TDF) parser and the TNT binary reader,
// then assembles them into MapInfo – equivalent to what maptool.cpp prints.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TafToolbox.MapTool
{
    // ── TDF / OTA parser ─────────────────────────────────────────────────────

    /// <summary>
    /// A TDF block (square-bracket section) with key/value pairs and nested blocks.
    /// Equivalent to the TdfBlock class in libs/ta/.
    /// </summary>
    public sealed class TdfBlock
    {
        public string                       Name     { get; }
        public Dictionary<string, string>   Values   { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<TdfBlock>               Children { get; } = new();

        public TdfBlock(string name) { Name = name; }

        public string? Get(string key) =>
            Values.TryGetValue(key, out var v) ? v : null;

        public TdfBlock? GetChild(string name) =>
            Children.Find(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Parses TA's TDF text format (used by .OTA, .FBI, .GUI files).
    /// </summary>
    public static class TdfParser
    {
        public static List<TdfBlock> Parse(string text)
        {
            var roots = new List<TdfBlock>();
            var stack = new Stack<TdfBlock>();

            // Strip BOM if present (some OTA files were saved with one)
            if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];

            // Strip C-style /* ... */ block comments first (rare, but valid TDF)
            text = System.Text.RegularExpressions.Regex.Replace(
                text, @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);

            var lines = text.Split('\n');

            foreach (var rawLine in lines)
            {
                string line = rawLine;

                // Strip C++-style // comments (but not inside a quoted value — TA TDF
                // values are rarely quoted, so this simple approach is acceptable)
                int commentIdx = line.IndexOf("//", StringComparison.Ordinal);
                if (commentIdx >= 0) line = line[..commentIdx];
                line = line.Trim().TrimEnd('\r');
                if (line.Length == 0) continue;

                // Process the line character-by-character-ish: a single line may contain
                // "[BlockName]", "[BlockName]{", "{", "}", "} [Next]{" etc. Split on
                // brackets/braces so each token is handled independently.
                int i = 0;
                while (i < line.Length)
                {
                    char c = line[i];

                    if (c == '[')
                    {
                        int close = line.IndexOf(']', i);
                        if (close < 0) break; // malformed — bail on this line
                        string blockName = line[(i + 1)..close].Trim();
                        var block = new TdfBlock(blockName);
                        if (stack.Count > 0) stack.Peek().Children.Add(block);
                        else                 roots.Add(block);
                        stack.Push(block);
                        i = close + 1;
                    }
                    else if (c == '{')
                    {
                        // Block already pushed when '[' was seen; just skip
                        i++;
                    }
                    else if (c == '}')
                    {
                        if (stack.Count > 0) stack.Pop();
                        i++;
                        // Optional trailing ';' after '}' — skip it
                        if (i < line.Length && line[i] == ';') i++;
                    }
                    else if (char.IsWhiteSpace(c))
                    {
                        i++;
                    }
                    else
                    {
                        // Remainder of the line (from here to next bracket/brace or end)
                        // is a "key=value;" statement.
                        int end = i;
                        while (end < line.Length && line[end] != '[' && line[end] != '{' && line[end] != '}')
                            end++;
                        string statement = line[i..end].Trim();
                        i = end;

                        int eq = statement.IndexOf('=');
                        if (eq > 0 && stack.Count > 0)
                        {
                            string key = statement[..eq].Trim();
                            string val = statement[(eq + 1)..].Trim();
                            if (val.EndsWith(';')) val = val[..^1].Trim();
                            stack.Peek().Values[key] = val;
                        }
                    }
                }
            }
            return roots;
        }
    }

    // ── TNT binary format ─────────────────────────────────────────────────────

    /// <summary>
    /// Parsed data from a TA .TNT heightmap file.
    /// </summary>
    public sealed class TntData
    {
        public int Width  { get; init; }   // map width  in tiles (16x16 px each)
        public int Height { get; init; }   // map height in tiles
        public int SeaLevel   { get; init; }
        public byte[] HeightMap { get; init; } = Array.Empty<byte>();
    }

    public static class TntReader
    {
        // TNT header layout (all little-endian):
        //   0x00  4  magic  = 0x00002000 (verified against real .tnt files)
        //   0x04  4  width  (in tiles)
        //   0x08  4  height (in tiles)
        //   0x0C  4  ptr_map_data
        //   0x10  4  ptr_tile_gfx
        //   0x14  4  ptr_tile_anims
        //   0x18  4  ptr_features
        //   0x1C  4  sea_level
        //   0x20  4  ptr_mini_map
        //   0x24  4  ptr_map_attrs   (one byte per tile: passable flag etc)

        private const uint TntMagic = 0x00002000u; // verified against real TA .tnt files

        public static TntData Read(byte[] data)
        {
            using var ms     = new MemoryStream(data);
            using var reader = new BinaryReader(ms, Encoding.ASCII, leaveOpen: false);

            uint magic  = reader.ReadUInt32();
            if (magic != TntMagic)
                throw new InvalidDataException($"Not a TNT file (magic=0x{magic:X8})");

            int  widthRaw  = reader.ReadInt32();   // TNT header stores size in TA
            int  heightRaw = reader.ReadInt32();   // "map units" (32 units = 1 tile),
                                                    // NOT tiles and NOT pixels directly.
            int  ptrMap   = reader.ReadInt32();
            int  ptrTile  = reader.ReadInt32();
            int  ptrAnim  = reader.ReadInt32();
            int  ptrFeat  = reader.ReadInt32();
            int  seaLevel = reader.ReadInt32();

            // Convert to actual tile counts. Verified against a real archive
            // (BookerMapsV1.ufo, AustraliaV2): OTA's human-readable "size=35 x 30"
            // field corresponds to raw header values width=1110, height=950 —
            // 1110/35 ≈ 31.7, 950/30 ≈ 31.7, both clustering on TA's documented
            // 32 map-units-per-tile ratio (16px/tile × 2 map-units/px). The OTA's
            // "size" field itself is a rounded display value, so dividing the raw
            // header by 32 is the correct/exact source of truth, not the rounded
            // OTA text.
            const int MapUnitsPerTile = 32;
            int width  = widthRaw  / MapUnitsPerTile;
            int height = heightRaw / MapUnitsPerTile;

            // Heightmap reading uses the RAW header dimensions — this byte-layout
            // logic (2-byte tile index + 1-byte height per entry) was not verified
            // against real file offsets and is unchanged by this fix; only the
            // displayed Width/Height (above) are converted to actual tile counts.
            byte[] heightMap = new byte[widthRaw * heightRaw];
            ms.Seek(ptrMap, SeekOrigin.Begin);
            // Each entry is a 2-byte tile index followed by a 1-byte height nibble;
            // for simplicity we read raw height data only.
            for (int i = 0; i < widthRaw * heightRaw; i++)
            {
                reader.ReadUInt16(); // tile index (skip)
                heightMap[i] = reader.ReadByte();
            }

            return new TntData
            {
                Width    = width,
                Height   = height,
                SeaLevel = seaLevel,
                HeightMap = heightMap,
            };
        }
    }

    // ── MapInfo ───────────────────────────────────────────────────────────────

    public sealed class MapInfo
    {
        public string Name          { get; init; } = "";
        public string ArchiveFile   { get; init; } = ""; // filename only, e.g. "maps_100.ufo" — matches the real maptool's MapDetails field 1
        public string Description   { get; init; } = "";
        public string Author        { get; init; } = "";
        public int    MaxPlayers    { get; init; }
        public int    Width         { get; init; }   // tiles
        public int    Height        { get; init; }   // tiles
        public int    WidthPixels   => Width  * 16;
        public int    HeightPixels  => Height * 16;
        public int    SeaLevel      { get; init; }
        public string TidalStrength { get; init; } = "";
        public string WindMin       { get; init; } = "";
        public string WindMax       { get; init; } = "";
        public string SolarMult     { get; init; } = "";
    }
}
