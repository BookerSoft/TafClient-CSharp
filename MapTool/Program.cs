// maptool – C# port of apps/maptool (maptool.cpp)
//
// Inspects Total Annihilation map archives (.HPI, .CCX, .UFO, .GP3) or
// directories and prints human-readable info about each .OTA / .TNT pair.
//
// Usage:
//   maptool <path> [--json]
//
//   path   A directory or archive file to scan.
//   --json Output JSON instead of plain text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using TafToolbox.Hpi;

namespace TafToolbox.MapTool
{
    /// <summary>
    /// maptool's Main entry point AND a reusable library surface
    /// (BuildMapDetails) — made public specifically so GpgNetApp can call
    /// into the same map-finding/hashing/formatting logic directly,
    /// in-process, rather than needing to shell out to a separately-built
    /// executable (the same pattern MapService already uses for the main
    /// client's own map scanning).
    ///
    /// MapDetails field format, confirmed directly from a real Java client
    /// log (game_178402.log, getMapDetails output) for "[100] Sandy Bridge":
    ///   0: mapname                "[100] Sandy Bridge"
    ///   1: archive filename       "maps_100.ufo"
    ///   2: hash (8 hex, lowercase)"3e009969"
    ///   3: description            "20 x 20 Map. W 12/30 T 26"
    ///   4: dimensions             "21 x 22"            (tiles — see caveat below)
    ///   5: start positions        "2, 4, 6, 8, 10"     (NOT YET PARSED — placeholder)
    ///   6: mineral range          "2000-5000"          (NOT YET PARSED — placeholder)
    ///   7: tidal strength         "26"
    ///   8: unknown/trailing       "112"                (meaning unconfirmed — placeholder)
    ///
    /// IMPORTANT — fields 5, 6, and 8 are NOT reliably derivable from what
    /// this OTA/TNT parser currently extracts. Start positions and mineral
    /// range appear to come from the OTA's [SCHEMA] sub-blocks, which this
    /// parser has never read. Rather than guess at an unverified binary
    /// format, these are populated with clearly-fake placeholder values so
    /// a downstream consumer isn't silently given wrong-but-plausible data.
    /// Field 4 (dimensions) is reported as Width x Height directly from the
    /// TNT header; the real log shows "21 x 22" for a map this parser would
    /// likely report as "20 x 20" (per the description string's own
    /// "20 x 20 Map" text) — there may be an off-by-one or different unit
    /// conversion in the real tool that hasn't been reconciled here.
    /// </summary>
    public static class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            // --gamepath/--mapname/--hash mode: prints a single \x1F-delimited
            // MapDetails line for one specific map, matching exactly what
            // gpgnet4ta's getMapDetails() invokes (confirmed directly from a
            // real Java client log: "maptool.exe --gamepath ... --mapname ...
            // --hash" producing
            // "name\x1Farchive\x1Fhash\x1Fdesc\x1Fdims\x1Fstartpos\x1Fmineral\x1Ftidal\x1Funknown").
            // This is a separate mode from the directory-scan/--json mode
            // below, which is this tool's own original C# port scope.
            if (Array.IndexOf(args, "--hash") >= 0)
                return RunHashMode(args);

            bool   outputJson = false;
            string path       = "";

            foreach (var arg in args)
            {
                if (arg == "--json") outputJson = true;
                else                 path = arg;
            }

            if (string.IsNullOrEmpty(path))
            {
                PrintUsage();
                return 1;
            }

            var maps = new List<MapInfo>();

            if (File.Exists(path))
            {
                // Single archive
                CollectMapsFromArchive(path, maps);
            }
            else if (Directory.Exists(path))
            {
                // Directory – scan top-level files and one level of sub-dirs
                foreach (string file in Directory.GetFiles(path, "*.*", SearchOption.TopDirectoryOnly))
                    if (IsArchive(file))
                        CollectMapsFromArchive(file, maps);

                foreach (string subDir in Directory.GetDirectories(path))
                    foreach (string file in Directory.GetFiles(subDir, "*.*", SearchOption.TopDirectoryOnly))
                        if (IsArchive(file))
                            CollectMapsFromArchive(file, maps);
            }
            else
            {
                Console.Error.WriteLine($"ERROR: Path not found: {path}");
                return 1;
            }

            if (outputJson)
                PrintJson(maps);
            else
                PrintText(maps);

            return 0;
        }

        // ── Archive scanning ──────────────────────────────────────────────────

        private static bool IsArchive(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".hpi" or ".ccx" or ".ufo" or ".gp3";
        }

        private static void CollectMapsFromArchive(string archivePath, List<MapInfo> maps)
        {
            try
            {
                using var fs      = File.OpenRead(archivePath);
                var       archive = new HpiArchive(fs);

                // Collect all .OTA files; pair each with its .TNT counterpart
                var otaFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                var tntFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

                CollectFiles(archive, archive.Root, "", otaFiles, tntFiles);

                foreach (var kvp in otaFiles)
                {
                    string baseName = Path.GetFileNameWithoutExtension(kvp.Key);
                    byte[] otaData  = kvp.Value;

                    tntFiles.TryGetValue(baseName, out byte[]? tntData);

                    var info = ParseMap(baseName, otaData, tntData, Path.GetFileName(archivePath));
                    if (info != null) maps.Add(info);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"WARNING: Could not read {archivePath}: {ex.Message}");
            }
        }

        private static void CollectFiles(
            HpiArchive                       archive,
            HpiArchive.DirectoryEntry        dir,
            string                           currentPath,
            Dictionary<string, byte[]>       otaFiles,
            Dictionary<string, byte[]>       tntFiles)
        {
            foreach (var entry in dir.Entries)
            {
                string fullPath = (currentPath + "/" + entry.Name).TrimStart('/');

                if (entry.File != null)
                {
                    string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                    if (ext == ".ota" || ext == ".tnt")
                    {
                        byte[] buf = new byte[entry.File.Size];
                        archive.Extract(entry.File, buf);

                        string baseName = Path.GetFileNameWithoutExtension(entry.Name);
                        if (ext == ".ota") otaFiles[baseName] = buf;
                        else               tntFiles[baseName] = buf;
                    }
                }
                else if (entry.Directory != null)
                {
                    CollectFiles(archive, entry.Directory, fullPath, otaFiles, tntFiles);
                }
            }
        }

        // ── Map parsing ───────────────────────────────────────────────────────

        private static MapInfo? ParseMap(string name, byte[] otaData, byte[]? tntData, string archiveFile = "")
        {
            try
            {
                string text   = Encoding.ASCII.GetString(otaData);
                var    blocks = TdfParser.Parse(text);

                // OTA root section: [GlobalHeader]
                TdfBlock? header = null;
                foreach (var b in blocks)
                {
                    if (string.Equals(b.Name, "GlobalHeader", StringComparison.OrdinalIgnoreCase))
                    { header = b; break; }
                }
                if (header == null && blocks.Count > 0) header = blocks[0];
                if (header == null) return null;

                int width  = 0, height = 0, seaLevel = 0;
                if (tntData != null)
                {
                    try
                    {
                        var tnt = TntReader.Read(tntData);
                        width    = tnt.Width;
                        height   = tnt.Height;
                        seaLevel = tnt.SeaLevel;
                    }
                    catch { /* TNT parse failure is non-fatal */ }
                }

                return new MapInfo
                {
                    Name          = header.Get("missionname")    ?? name,
                    ArchiveFile   = archiveFile,
                    Description   = header.Get("missiondesc")    ?? "",
                    Author        = header.Get("author")         ?? "",
                    MaxPlayers    = int.TryParse(header.Get("numplayers"), out int np) ? np : 0,
                    Width         = width,
                    Height        = height,
                    SeaLevel      = seaLevel,
                    TidalStrength = header.Get("tidalstrength")  ?? "",
                    WindMin       = header.Get("minwindspeed")   ?? "",
                    WindMax       = header.Get("maxwindspeed")   ?? "",
                    SolarMult     = header.Get("solarmultiplier")?? "",
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"WARNING: Failed to parse map {name}: {ex.Message}");
                return null;
            }
        }

        // ── Output ────────────────────────────────────────────────────────────

        private static void PrintText(List<MapInfo> maps)
        {
            if (maps.Count == 0)
            {
                Console.WriteLine("No maps found.");
                return;
            }
            foreach (var m in maps)
            {
                Console.WriteLine($"Name:          {m.Name}");
                Console.WriteLine($"Description:   {m.Description}");
                Console.WriteLine($"Author:        {m.Author}");
                Console.WriteLine($"Players:       {m.MaxPlayers}");
                if (m.Width > 0)
                {
                    Console.WriteLine($"Size (tiles):  {m.Width} x {m.Height}");
                    Console.WriteLine($"Size (pixels): {m.WidthPixels} x {m.HeightPixels}");
                    Console.WriteLine($"Sea level:     {m.SeaLevel}");
                }
                Console.WriteLine($"Tidal:         {m.TidalStrength}");
                Console.WriteLine($"Wind:          {m.WindMin} – {m.WindMax}");
                Console.WriteLine($"Solar mult:    {m.SolarMult}");
                Console.WriteLine(new string('-', 40));
            }
        }

        private static void PrintJson(List<MapInfo> maps)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            Console.WriteLine(JsonSerializer.Serialize(maps, options));
        }

        /// <summary>
        /// Core logic for the --hash mode, callable directly without CLI
        /// args — used by GpgNetApp to build a real MapDetails string
        /// in-process, the same way MapService already calls into this
        /// project's parsing logic directly rather than shelling out to a
        /// separate executable. Returns null if the map can't be found or
        /// hashed; check stderr/exceptions via the try/catch at call sites
        /// if more detail is needed.
        ///
        /// See RunHashMode's doc comment below for the full field-format
        /// explanation and the caveat about fields 5/6/8 being placeholders.
        /// </summary>
        public static string? BuildMapDetails(string gamePath, string mapName)
        {
            if (!Directory.Exists(gamePath))
            {
                Console.Error.WriteLine($"[MapTool.BuildMapDetails] gamepath does not exist: '{gamePath}'");
                return null;
            }

            string? foundArchivePath = null;
            MapInfo? foundMap = null;
            var triedNames = new List<string>();

            // Top-level archives first (matches MapService's own scan depth
            // exactly — confirmed via MapService.cs's RefreshMod using
            // Directory.EnumerateFiles(gameRoot) with no SearchOption, i.e.
            // top-level only, so this should already match whatever the
            // host dialog's own map list found).
            var candidateFiles = new List<string>(Directory.GetFiles(gamePath, "*.*", SearchOption.TopDirectoryOnly));
            // Fallback: also check one level of subdirectories, matching
            // this tool's OWN directory-mode scan depth (see Main below) —
            // in case a map genuinely lives nested one level deep rather
            // than directly in gamepath.
            foreach (string subDir in Directory.GetDirectories(gamePath))
                candidateFiles.AddRange(Directory.GetFiles(subDir, "*.*", SearchOption.TopDirectoryOnly));

            foreach (string file in candidateFiles)
            {
                if (!IsArchive(file)) continue;
                var maps = new List<MapInfo>();
                CollectMapsFromArchive(file, maps);
                foreach (var m in maps) triedNames.Add(m.Name);
                var match = maps.Find(m => string.Equals(m.Name, mapName, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    foundArchivePath = file;
                    foundMap = match;
                    break;
                }
            }

            if (foundMap is null || foundArchivePath is null)
            {
                Console.Error.WriteLine($"[MapTool.BuildMapDetails] map not found: '{mapName}' " +
                    $"(searched {candidateFiles.Count} files in '{gamePath}', " +
                    $"found {triedNames.Count} maps total: {string.Join(", ", triedNames.Take(20))}" +
                    (triedNames.Count > 20 ? ", ..." : "") + ")");
                return null;
            }

            string hash;
            try
            {
                byte[] archiveBytes = File.ReadAllBytes(foundArchivePath);
                hash = TafToolbox.Crc32.Crc32Calculator.ComputeHex(archiveBytes);
            }
            catch
            {
                return null;
            }

            string dimensions = $"{foundMap.Width} x {foundMap.Height}";
            const string startPositionsPlaceholder  = "0";
            const string mineralRangePlaceholder    = "0-0";
            const string unknownTrailingPlaceholder = "0";

            return string.Join('\x1F', new[]
            {
                foundMap.Name,
                foundMap.ArchiveFile,
                hash,
                foundMap.Description,
                dimensions,
                startPositionsPlaceholder,
                mineralRangePlaceholder,
                foundMap.TidalStrength,
                unknownTrailingPlaceholder,
            });
        }

        private static int RunHashMode(string[] args)
        {
            string gamePath = "";
            string mapName  = "";
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--gamepath" && i + 1 < args.Length) gamePath = args[++i];
                else if (args[i] == "--mapname" && i + 1 < args.Length) mapName = args[++i];
            }

            if (string.IsNullOrEmpty(gamePath) || string.IsNullOrEmpty(mapName))
            {
                Console.Error.WriteLine("ERROR: --hash mode requires --gamepath and --mapname");
                return 1;
            }

            string? mapDetails = BuildMapDetails(gamePath, mapName);
            if (mapDetails is null)
            {
                Console.Error.WriteLine($"ERROR: map not found or could not be hashed: {mapName}");
                return 1;
            }

            Console.WriteLine(mapDetails);
            return 0;
        }

        private static void PrintUsage()
        {
            Console.Error.WriteLine("maptool – inspect Total Annihilation map archives");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Usage: maptool <path> [--json]");
            Console.Error.WriteLine("       maptool --gamepath <dir> --mapname <name> --hash");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  path        Directory or HPI/CCX/UFO/GP3 archive to scan.");
            Console.Error.WriteLine("  --json      Emit JSON instead of plain text.");
            Console.Error.WriteLine("  --hash      Print a single \\x1F-delimited MapDetails line for one");
            Console.Error.WriteLine("              map (matches gpgnet4ta's getMapDetails invocation).");
            Console.Error.WriteLine("              Requires --gamepath and --mapname.");
        }
    }
}
