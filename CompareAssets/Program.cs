// compare_assets – C# port of compare_assets.cpp
//
// Finds files whose CRC-32 hashes match between a source directory and
// a set of target HPI/CCX/UFO/GP3 archives.  Optionally filters to only
// report matches that are exclusive to a given target sub-directory (mod).
//
// Usage:
//   compare_assets <source-dir> <target-dir> <mod-filter>
//
//   mod-filter: a semicolon-separated list of sub-directory names (or
//               substrings of archive paths) to restrict results to.
//               Pass "*" or "." to match everything.
//
// Example:
//   compare_assets C:\ta\unpacked C:\ta\archives "totala1;totala2"

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TafToolbox.Crc32;
using TafToolbox.Hpi;

namespace TafToolbox.CompareAssets
{
    internal static class Program
    {
        // Supported TA archive extensions (case-insensitive)
        private static readonly string[] ArchiveExtensions =
            { ".ccx", ".hpi", ".gp3", ".ufo" };

        static int Main(string[] args)
        {
            if (args.Length != 3)
            {
                Console.Error.WriteLine("compare_assets 1.0");
                Console.Error.WriteLine("Find files with matching CRC-32 hashes between a source");
                Console.Error.WriteLine("directory and target HPI/CCX/UFO/GP3 archives.");
                Console.Error.WriteLine();
                Console.Error.WriteLine("Usage: compare_assets <source-dir> <target-dir> <mod>");
                Console.Error.WriteLine();
                Console.Error.WriteLine("  source    Source directory to scan recursively.");
                Console.Error.WriteLine("  target    Directory containing archives to scan.");
                Console.Error.WriteLine("  mod       Semicolon-separated list of target mod sub-strings.");
                Console.Error.WriteLine("            Use '*' to match all archives.");
                return 1;
            }

            string   sourceRoot = args[0];
            string   targetRoot = args[1];
            string[] targetMods = args[2].Split(';', StringSplitOptions.RemoveEmptyEntries);

            if (!Directory.Exists(sourceRoot))
            {
                Console.Error.WriteLine($"ERROR: Source directory not found: {sourceRoot}");
                return 1;
            }
            if (!Directory.Exists(targetRoot))
            {
                Console.Error.WriteLine($"ERROR: Target directory not found: {targetRoot}");
                return 1;
            }

            // ── Step 1: Build CRC32 → archive-path:internal-path map ──────────
            // (multimap: one CRC can appear in multiple archives)
            var targetMap = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            // Archives in the root directory
            foreach (string archivePath in GetArchivePaths(targetRoot, recursive: false))
                IndexArchive(archivePath, targetMap);

            // Archives one level deep (immediate sub-directories)
            foreach (string subDir in Directory.GetDirectories(targetRoot))
                foreach (string archivePath in GetArchivePaths(subDir, recursive: false))
                    IndexArchive(archivePath, targetMap);

            Console.Error.WriteLine($"Indexed {targetMap.Count} unique CRC values from target archives.");

            // ── Step 2: Walk source directory and look up each file ───────────
            var sourceFiles = Directory.GetFiles(sourceRoot, "*.*",
                                                 SearchOption.AllDirectories);

            foreach (string sourcePath in sourceFiles)
            {
                byte[] data;
                try
                {
                    data = File.ReadAllBytes(sourcePath);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"WARNING: Cannot read {sourcePath}: {ex.Message}");
                    continue;
                }

                string crc = Crc32Calculator.ComputeHex(data);

                if (!targetMap.TryGetValue(crc, out var matches))
                    continue;

                // Two-pass logic matching the C++ original:
                //   Pass 0: count how many matches are inside one of the target mods.
                //   Only print if *all* matches are inside the requested mod(s)
                //   (i.e. the file is exclusive to those mods).
                bool isExclusive;

                if (targetMods.Length == 0 || (targetMods.Length == 1 &&
                    (targetMods[0] == "*" || targetMods[0] == ".")))
                {
                    isExclusive = true;
                }
                else
                {
                    int modMatchCount = matches.Count(m =>
                        targetMods.Any(mod =>
                            m.Contains(mod, StringComparison.OrdinalIgnoreCase)));
                    isExclusive = modMatchCount == matches.Count;
                }

                if (!isExclusive) continue;

                Console.WriteLine();
                Console.WriteLine($"Match for: {sourcePath} (CRC32: {crc})");
                foreach (string m in matches)
                    Console.WriteLine($"    => {m}");
            }

            return 0;
        }

        // ── Archive discovery ─────────────────────────────────────────────────

        private static IEnumerable<string> GetArchivePaths(string directory, bool recursive)
        {
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            return Directory.GetFiles(directory, "*", option)
                .Where(f => ArchiveExtensions.Contains(
                    Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
        }

        // ── Archive indexing ──────────────────────────────────────────────────

        private static void IndexArchive(string archivePath,
                                         Dictionary<string, List<string>> targetMap)
        {
            Console.Error.WriteLine($"Processing archive: {archivePath}");
            try
            {
                using var fs = File.OpenRead(archivePath);
                var archive = new HpiArchive(fs);
                IndexDirectory(archive, archive.Root, "", archivePath, targetMap);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  WARNING: Failed to read {archivePath}: {ex.Message}");
            }
        }

        private static void IndexDirectory(
            HpiArchive                          archive,
            HpiArchive.DirectoryEntry           dir,
            string                              currentPath,
            string                              archivePath,
            Dictionary<string, List<string>>    targetMap)
        {
            foreach (var entry in dir.Entries)
            {
                string fullPath = currentPath + "/" + entry.Name;

                if (entry.File != null)
                {
                    var fileEntry = entry.File;
                    try
                    {
                        byte[] buffer = new byte[fileEntry.Size];
                        archive.Extract(fileEntry, buffer);

                        string crc      = Crc32Calculator.ComputeHex(buffer);
                        string location = archivePath + ":" + fullPath;

                        if (!targetMap.TryGetValue(crc, out var list))
                        {
                            list = new List<string>();
                            targetMap[crc] = list;
                        }
                        list.Add(location);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            $"  WARNING: Failed to extract {fullPath} from {archivePath}: {ex.Message}");
                    }
                }
                else if (entry.Directory != null)
                {
                    IndexDirectory(archive, entry.Directory, fullPath, archivePath, targetMap);
                }
            }
        }
    }
}
