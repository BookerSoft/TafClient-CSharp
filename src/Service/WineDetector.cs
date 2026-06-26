using System.Runtime.InteropServices;

namespace TafClient.Service;

/// <summary>
/// Detects Wine availability on macOS and Linux and prepends it to
/// process command lists when launching Windows executables (.exe).
///
/// Mirrors the Java client's pattern in TotalAnnihilationService:
///   if (org.bridj.Platform.isLinux()) { command.add("wine"); }
///
/// On macOS, CrossOver and Whisky package wine as a bundle app;
/// plain Wine from Homebrew is also supported.
/// On Linux, wine / wine64 / wine-stable are checked in PATH.
/// </summary>
public static class WineDetector
{
    private static string? _cachedWinePath;   // null = not yet searched
    private static bool    _searched;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>True if we're on a non-Windows OS where Wine is required.</summary>
    public static bool NeedsWine =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    /// <summary>True if NeedsWine is true AND a Wine executable was found.</summary>
    public static bool WineAvailable => NeedsWine && FindWine() is not null;

    /// <summary>
    /// Returns the full path to the wine executable, or null if not found.
    /// Result is cached after the first call.
    /// </summary>
    public static string? FindWine()
    {
        if (_searched) return _cachedWinePath;
        _searched      = true;
        _cachedWinePath = Locate();
        return _cachedWinePath;
    }

    /// <summary>
    /// Prepends wine (if needed and available) to a command list, mirroring:
    ///   if (Platform.isLinux()) command.add(0, "wine");
    ///
    /// Returns the modified list.  If Wine is not found, returns the original
    /// list unchanged so callers can still try to run the exe directly
    /// (useful in tests / CI where wine isn't installed).
    /// </summary>
    public static List<string> PrependWine(List<string> args)
    {
        if (!NeedsWine) return args;

        string? wine = FindWine();
        if (wine is null)
        {
            Console.WriteLine("[WINE] WARNING: Wine not found — running .exe directly (will likely fail)");
            return args;
        }

        var result = new List<string>(args.Count + 1) { wine };
        result.AddRange(args);
        return result;
    }

    /// <summary>
    /// Returns the status string suitable for displaying in the settings tab.
    /// </summary>
    public static string StatusString()
    {
        if (!NeedsWine)   return "Not required (Windows)";
        string? w = FindWine();
        return w is not null ? $"Found: {w}" : "Not found — install Wine to play";
    }

    // ── Detection ─────────────────────────────────────────────────────────────

    private static string? Locate()
    {
        if (!NeedsWine) return null;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return LocateMacOS();
        else
            return LocateLinux();
    }

    // ── macOS: CrossOver, Whisky, Homebrew, MacPorts ──────────────────────────

    private static string? LocateMacOS()
    {
        // 0. Our own bundled Wine distribution, staged into the .app bundle
        //    by build-macos-app.sh from deps/mac/bundle/Resources/wine/ —
        //    Contents/MacOS/TafClient (AppContext.BaseDirectory) -> sibling
        //    Contents/Resources/wine/bin/wine. Checked FIRST so a user with
        //    our bundled Wine doesn't need CrossOver/Whisky/Homebrew
        //    installed separately at all — this is the whole point of
        //    shipping our own Wine.
        string bundledWine = Path.Combine(AppContext.BaseDirectory, "..", "Resources", "wine", "bin", "wine");
        if (File.Exists(bundledWine)) return Path.GetFullPath(bundledWine);

        // 1. CrossOver (most common on macOS for gaming)
        //    /Applications/CrossOver.app/.../wine
        foreach (var crossover in new[]
        {
            "/Applications/CrossOver.app/Contents/SharedSupport/CrossOver/bin/wine",
            "/Applications/CrossOver.app/Contents/SharedSupport/CrossOver/bin/wine64",
        })
        {
            if (File.Exists(crossover)) return crossover;
        }

        // 2. Whisky (popular newer Mac wine wrapper)
        //    ~/Library/Application Support/Whisky/Libraries/Wine/bin/wine
        string whiskyBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "Whisky", "Libraries", "Wine", "bin");
        foreach (var candidate in new[] { "wine64", "wine" })
        {
            string p = Path.Combine(whiskyBase, candidate);
            if (File.Exists(p)) return p;
        }

        // 3. Homebrew (arm64 and x86_64 prefixes)
        foreach (var brewBase in new[]
        {
            "/opt/homebrew/bin",    // Apple Silicon
            "/usr/local/bin",       // Intel
        })
        {
            foreach (var candidate in new[] { "wine64", "wine", "wine-stable" })
            {
                string p = Path.Combine(brewBase, candidate);
                if (File.Exists(p)) return p;
            }
        }

        // 4. MacPorts
        foreach (var candidate in new[] { "/opt/local/bin/wine64", "/opt/local/bin/wine" })
        {
            if (File.Exists(candidate)) return candidate;
        }

        // 5. Fall back to PATH search
        return FindInPath("wine64") ?? FindInPath("wine");
    }

    // ── Linux ─────────────────────────────────────────────────────────────────

    private static string? LocateLinux()
    {
        // Common package names: wine, wine64, wine-stable, wine-development, wine-staging
        foreach (var candidate in new[] { "wine64", "wine", "wine-stable", "wine-development", "wine-staging" })
        {
            string? found = FindInPath(candidate);
            if (found is not null) return found;
        }
        return null;
    }

    // ── PATH search ───────────────────────────────────────────────────────────

    private static string? FindInPath(string executable)
    {
        string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            string full = Path.Combine(dir, executable);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    // ── Wine version string (for display) ────────────────────────────────────

    public static string? GetWineVersion()
    {
        string? wine = FindWine();
        if (wine is null) return null;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(wine)
            {
                ArgumentList            = { "--version" },
                UseShellExecute         = false,
                RedirectStandardOutput  = true,
                RedirectStandardError   = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            string? ver = p?.StandardOutput.ReadLine();
            p?.WaitForExit(3000);
            return ver?.Trim();
        }
        catch { return null; }
    }

    // ── WINEPREFIX helpers ────────────────────────────────────────────────────

    /// <summary>
    /// The real filesystem directory Wine treats as the C: drive for TAF's
    /// dedicated prefix — standard Wine convention, {prefix}/drive_c.
    /// Created if it doesn't exist yet (a fresh prefix won't have it until
    /// Wine itself has been run at least once, but the Install Mod flow may
    /// need to copy into it before that's happened).
    /// </summary>
    public static string GetWineDriveC()
    {
        string dir = Path.Combine(GetWinePrefix(), "drive_c");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Converts a real filesystem path that lives under the Wine prefix's
    /// drive_c into the Windows-style "C:\..." path Wine itself would see
    /// for that same location — e.g.
    /// "/Users/you/.wine-taf/drive_c/TAF/tavmod" -> "C:\TAF\tavmod".
    /// Returns null if realPath isn't actually under drive_c, since the
    /// conversion is only meaningful for paths we've copied into the prefix.
    /// </summary>
    public static string? ToWindowsPath(string realPath)
    {
        string driveC = GetWineDriveC();
        string fullReal = Path.GetFullPath(realPath);
        string fullDriveC = Path.GetFullPath(driveC);
        if (!fullReal.StartsWith(fullDriveC, StringComparison.Ordinal)) return null;

        string relative = fullReal[fullDriveC.Length..].TrimStart('/', '\\');
        string windowsRelative = relative.Replace('/', '\\');
        return windowsRelative.Length > 0 ? $@"C:\{windowsRelative}" : "C:\\";
    }

    /// <summary>
    /// Converts a real macOS/Linux filesystem path into a Wine-resolvable
    /// Windows-style path, for use when actually launching a process VIA
    /// wine (e.g. `wine C:\TAF\tavmod\TotalA.exe`, NOT
    /// `wine /Users/you/Custom/102025/TotalA.exe`). This is different from
    /// ToWindowsPath above, which only handles paths already inside the
    /// prefix's own drive_c (used for Install Mod's display/diagnostics) —
    /// this one handles ANY real path, including the user's own arbitrary
    /// install folders that were never copied into the prefix at all.
    ///
    /// If the path IS inside drive_c, returns the C:\... form (delegates to
    /// ToWindowsPath). Otherwise falls back to the standard Wine convention
    /// that Z:\ maps to the real filesystem root / by default — confirmed
    /// via this session's earlier investigation into talauncher's own
    /// DPlayReg path resolution, which relies on exactly this same mapping.
    /// </summary>
    public static string ToWineArgPath(string realPath)
    {
        string fullReal = Path.GetFullPath(realPath);

        string? cDrivePath = ToWindowsPath(fullReal);
        if (cDrivePath is not null) return cDrivePath;

        // Z:\ + the real path with backslashes instead of forward slashes,
        // minus the leading "/". E.g. "/Users/you/Custom/102025/TotalA.exe"
        // -> "Z:\Users\you\Custom\102025\TotalA.exe".
        string zRelative = fullReal.TrimStart('/').Replace('/', '\\');
        return $@"Z:\{zRelative}";
    }

    /// <summary>
    /// Returns the WINEPREFIX to use for TAF.
    /// Checks TAF_WINEPREFIX env var, then defaults to ~/.wine-taf.
    /// A dedicated prefix keeps TAF isolated from other Wine apps.
    /// </summary>
    public static string GetWinePrefix()
    {
        string? env = Environment.GetEnvironmentVariable("TAF_WINEPREFIX");
        if (!string.IsNullOrEmpty(env)) return env;

        string? bundlePrefix = GetBundleContainedPrefix();
        if (bundlePrefix is not null) return bundlePrefix;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".wine-taf");
    }

    /// <summary>
    /// If the running executable is inside a macOS .app bundle
    /// (AppContext.BaseDirectory ends in .../Contents/MacOS/), returns
    /// .../Contents/Resources/prefix — keeping the Wine prefix fully
    /// self-contained within the distributable bundle, the same way the
    /// bundled Wine binary itself is resolved relative to the bundle (see
    /// LocateMacOS's priority-0 check). Returns null if not running from a
    /// bundle, so callers fall back to the original ~/.wine-taf behavior
    /// (e.g. running from source during development, where there's no
    /// .app bundle to contain anything in).
    /// </summary>
    public static string? GetBundleContainedPrefix()
    {
        if (!OperatingSystem.IsMacOS()) return null;

        string baseDir = AppContext.BaseDirectory.TrimEnd('/', '\\');
        // Expect .../TafClient.app/Contents/MacOS — walk up two levels from
        // MacOS to Contents, then back down into Resources/prefix. Checking
        // for the literal ".app/Contents/MacOS" suffix is the standard,
        // reliable way to detect "am I running from inside a bundle" — a
        // plain folder/source-tree execution won't have this exact shape.
        if (!baseDir.EndsWith(Path.Combine(".app", "Contents", "MacOS"), StringComparison.Ordinal))
            return null;

        string contentsDir = Path.GetDirectoryName(baseDir) ?? "";
        if (string.IsNullOrEmpty(contentsDir)) return null;

        return Path.Combine(contentsDir, "Resources", "prefix");
    }

    /// <summary>
    /// Returns extra environment variables to set when launching Wine processes.
    /// </summary>
    public static Dictionary<string, string> GetWineEnvironment()
    {
        var env = new Dictionary<string, string>
        {
            ["WINEPREFIX"] = GetWinePrefix(),
        };

        // Suppress Wine debug spam unless TAF_WINE_DEBUG is set
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TAF_WINE_DEBUG")))
            env["WINEDEBUG"] = "-all";

        // If we're using our own bundled Wine (see LocateMacOS's priority-0
        // check), point it at its own bundled lib/ and wineserver explicitly
        // — a self-contained Wine distribution's dylibs aren't on the
        // system's default library search path, and without this it would
        // either fail to launch or accidentally pick up a different,
        // possibly incompatible wineserver/libs from some other Wine
        // install on the same machine.
        string? wine = FindWine();
        if (wine is not null && OperatingSystem.IsMacOS())
        {
            string wineBinDir = Path.GetDirectoryName(wine) ?? "";
            string wineRoot   = Path.GetFullPath(Path.Combine(wineBinDir, ".."));
            string libDir     = Path.Combine(wineRoot, "lib");
            string wineserver = Path.Combine(wineBinDir, "wineserver");
            if (Directory.Exists(libDir))
                env["DYLD_LIBRARY_PATH"] = libDir;
            if (File.Exists(wineserver))
                env["WINESERVER"] = wineserver;
        }

        return env;
    }
}
