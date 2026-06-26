using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace TafClient.Service;

/// <summary>
/// Creates and configures the TAF WINEPREFIX on first launch.
///
/// Based on WineHQ / Lutris / CrossOver documentation for Total Annihilation:
///
///   1. wineboot --init        — initialise the prefix structure
///   2. winecfg (registry)     — set Windows version to WinXP (TA requirement)
///   3. winetricks directplay  — DirectPlay DLLs required for TAF multiplayer
///   4. winecfg (registry)     — set sound driver to alsa/coreaudio
///   5. wineserver --wait      — wait for all background Wine processes to exit
///
/// The sentinel file ~/.wine-taf/.taf_setup_done is written on success so
/// the setup only runs once per prefix. Delete it to force a re-run.
///
/// NOTE: winetricks must be installed separately by the user.
/// If winetricks is not available we skip step 3 and warn in the UI.
/// The prefix will still work for single-player; multiplayer requires
/// DirectPlay which the user can install manually via:
///   WINEPREFIX=~/.wine-taf winetricks directplay
/// </summary>
public sealed class WinePrefixSetup
{
    private readonly ILogger<WinePrefixSetup> _log;

    public string  PrefixPath   { get; }       = WineDetector.GetWinePrefix();
    public string? WinePath     { get; }       = WineDetector.FindWine();
    public string  SentinelFile => Path.Combine(PrefixPath, ".taf_setup_done");

    /// <summary>
    /// True only if setup has completed AND was completed using the SAME
    /// Wine binary currently in use. The sentinel file's content is the
    /// Wine binary path that performed setup, not just a bare marker —
    /// without this check, switching Wine binaries (e.g. from an external
    /// Homebrew/CrossOver install to our own bundled Wine) against an
    /// already-initialized prefix would skip wineboot --init entirely for
    /// the new binary, since a sentinel from the OLD binary's setup would
    /// still satisfy a bare existence check. Confirmed as the likely cause
    /// of a fast, silent --registerdplay failure (exit code 1, no dialog,
    /// no log — registerdplay never writes one regardless, confirmed
    /// separately) the first time the bundled Wine ran against a prefix
    /// that had only ever been initialized by the external Wine before.
    /// </summary>
    public bool IsComplete
    {
        get
        {
            if (!File.Exists(SentinelFile)) return false;
            try
            {
                // Sentinel format (see the write site near the end of
                // SetupAsync): a few human-readable lines including
                // "Wine: {path}". Parse that line out rather than requiring
                // an exact whole-file match, so this works against both
                // sentinels written before and after this check existed.
                string content = File.ReadAllText(SentinelFile);
                string? wineLine = content
                    .Split('\n')
                    .FirstOrDefault(l => l.StartsWith("Wine:", StringComparison.Ordinal));
                if (wineLine is null) return false; // older/malformed sentinel with no recorded Wine path — treat as incomplete, safer to re-run
                string recordedWine = wineLine["Wine:".Length..].Trim();
                return string.Equals(recordedWine, WinePath, StringComparison.Ordinal);
            }
            catch
            {
                return false; // unreadable sentinel — treat as incomplete, safer to re-run setup than skip it
            }
        }
    }

    public bool IsNeeded   => WineDetector.NeedsWine && !IsComplete;

    public WinePrefixSetup(ILogger<WinePrefixSetup> log) => _log = log;

    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Runs the full Wine prefix setup asynchronously.
    /// Reports progress (0-100) and status strings through the callbacks.
    /// Returns true if setup completed successfully.
    /// </summary>
    public async Task<bool> SetupAsync(
        IProgress<(int pct, string status)>? progress = null,
        CancellationToken ct = default)
    {
        if (!WineDetector.NeedsWine)
        {
            _log.LogDebug("WinePrefixSetup: not needed on Windows");
            return true;
        }

        if (WinePath is null)
        {
            Report(progress, 0, "Wine not found — cannot set up prefix");
            _log.LogWarning("WinePrefixSetup: Wine not found");
            return false;
        }

        if (IsComplete)
        {
            _log.LogInformation("WinePrefixSetup: already done ({File})", SentinelFile);
            return true;
        }

        _log.LogInformation("WinePrefixSetup: starting for prefix={Prefix}", PrefixPath);
        Directory.CreateDirectory(PrefixPath);

        // ── Step 1: Initialise prefix ─────────────────────────────────────────
        // wineboot --init creates drive_c, registry hives, etc.
        Report(progress, 5, "Initialising Wine prefix…");
        bool ok = await RunWineAsync("wineboot", ["--init"], ct);
        if (!ok) { Report(progress, 5, "wineboot failed"); return false; }

        // Give wineserver a moment to settle
        await Task.Delay(1500, ct);

        // ── Step 2: Set Windows version to Windows XP ─────────────────────────
        // TA uses DirectX 5-era APIs; WinXP is the most compatible Wine target.
        // Done via reg.exe (part of Wine's built-in Windows tools).
        Report(progress, 20, "Configuring Windows version (XP)…");
        await SetRegistryKeyAsync(
            @"HKEY_CURRENT_USER\Software\Wine",
            "Version", "winxp", ct);

        // ── Step 3: Set Windows version in WinVer key (winecfg uses this) ──────
        await SetRegistryKeyAsync(
            @"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows NT\CurrentVersion",
            "CurrentVersion", "5.1", ct);
        await SetRegistryKeyAsync(
            @"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows NT\CurrentVersion",
            "CurrentBuildNumber", "2600", ct);
        await SetRegistryKeyAsync(
            @"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows NT\CurrentVersion",
            "CSDVersion", "Service Pack 3", ct);

        // ── Step 4: Configure audio (coreaudio on macOS, alsa on Linux) ────────
        Report(progress, 35, "Configuring audio driver…");
        string audioDriver = OperatingSystem.IsMacOS() ? "coreaudio" : "alsa";
        await SetRegistryKeyAsync(
            @"HKEY_CURRENT_USER\Software\Wine\Drivers",
            "Audio", audioDriver, ct);

        // ── Step 5: Disable Wine crash dialogs (cleaner for headless launch) ──
        await SetRegistryKeyAsync(
            @"HKEY_CURRENT_USER\Software\Wine\WineDbg",
            "ShowCrashDialog", "0", "REG_DWORD", ct);

        // ── Step 6: Override DirectPlay DLLs (native preferred) ───────────────
        // Required for TAF multiplayer. Installs dplayx.dll, dpnet.dll etc.
        // as native DLL overrides.
        Report(progress, 50, "Configuring DirectPlay DLL overrides…");
        await SetDllOverrideAsync("dplayx", "native,builtin", ct);
        await SetDllOverrideAsync("dpnet",  "native,builtin", ct);
        await SetDllOverrideAsync("dpnaddr","native,builtin", ct);
        await SetDllOverrideAsync("dpnhpast","native,builtin", ct);
        await SetDllOverrideAsync("dpwsockx","native,builtin", ct);

        // ── Step 7: winetricks directplay (if winetricks is available) ─────────
        // This downloads and installs the actual native DirectPlay DLLs.
        Report(progress, 60, "Installing DirectPlay (winetricks)…");
        string? winetricks = FindWinetricks();
        if (winetricks != null)
        {
            bool wtOk = await RunWinetricksAsync(winetricks, ["directplay"], ct);
            if (!wtOk)
                _log.LogWarning("WinePrefixSetup: winetricks directplay failed — multiplayer may not work");
        }
        else
        {
            _log.LogWarning("WinePrefixSetup: winetricks not found — skipping DirectPlay install");
            Report(progress, 65, "winetricks not found — skipping DirectPlay");
        }

        // ── Step 8: Disable Wine mono / gecko auto-install popups ─────────────
        Report(progress, 80, "Disabling Wine auto-install dialogs…");
        await SetRegistryKeyAsync(
            @"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Uninstall\Wine Mono",
            "DisplayName", "Wine Mono", ct);
        // Prevent Mono install dialog
        await RunWineAsync("reg", ["add",
            @"HKEY_CURRENT_USER\Software\Wine\WineAppLoader",
            "/v", "DisableBuiltinInstall", "/t", "REG_DWORD", "/d", "1", "/f"], ct);

        // ── Step 9: Wait for wineserver to idle ────────────────────────────────
        Report(progress, 90, "Waiting for Wine to settle…");
        await RunWineserverAsync(["--wait"], ct);
        await Task.Delay(500, ct);

        // ── Step 10: Make the whole prefix tree read/write for everyone ───────
        // Needed now that the prefix can live inside the .app bundle
        // (Contents/Resources/prefix) rather than always under the
        // invoking user's own home directory — a shared/multi-user
        // machine, or the bundle being copied between accounts, could
        // otherwise hit permission errors the first time a different user
        // tries to use it. "a+rwX" grants read/write to everyone; the
        // capital X only adds execute where it already applies (existing
        // directories, or files already marked executable) rather than
        // making every regular file spuriously executable.
        Report(progress, 95, "Setting prefix permissions…");
        await FixPrefixPermissionsAsync(ct);

        // ── Done ───────────────────────────────────────────────────────────────
        await File.WriteAllTextAsync(SentinelFile,
            $"TAF Wine prefix setup completed {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n" +
            $"Wine: {WinePath}\n" +
            $"Prefix: {PrefixPath}\n", ct);

        Report(progress, 100, "Wine prefix ready");
        _log.LogInformation("WinePrefixSetup: complete");
        return true;
    }

    /// <summary>
    /// Recursively grants read/write (and execute-where-applicable)
    /// permissions to everyone on the prefix directory tree. Best-effort —
    /// failures are logged but don't fail the overall setup, since a
    /// single-user machine (the common case) doesn't strictly need this
    /// and the prefix is still fully usable by the user who created it
    /// either way.
    /// </summary>
    private async Task FixPrefixPermissionsAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("chmod")
            {
                ArgumentList = { "-R", "a+rwX", PrefixPath },
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };
            using var p = Process.Start(psi);
            if (p is not null)
            {
                await p.WaitForExitAsync(ct);
                if (p.ExitCode != 0)
                {
                    string err = await p.StandardError.ReadToEndAsync(ct);
                    _log.LogWarning("WinePrefixSetup: chmod exited {Code}: {Err}", p.ExitCode, err);
                }
            }
        }
        catch (Exception ex)
        {
            // Not fatal — e.g. chmod might not exist on some minimal
            // environment, or the prefix might be on a filesystem that
            // doesn't support Unix permission bits at all.
            _log.LogWarning(ex, "WinePrefixSetup: failed to set prefix permissions (non-fatal)");
        }
    }

    // ── Registry helpers ──────────────────────────────────────────────────────

    private Task SetRegistryKeyAsync(string keyPath, string valueName,
        string value, CancellationToken ct = default)
        => SetRegistryKeyAsync(keyPath, valueName, value, "REG_SZ", ct);

    private async Task SetRegistryKeyAsync(string keyPath, string valueName,
        string value, string type, CancellationToken ct = default)
    {
        // Use Wine's built-in reg.exe
        await RunWineAsync("reg", [
            "add", keyPath,
            "/v", valueName,
            "/t", type,
            "/d", value,
            "/f"   // overwrite without prompt
        ], ct);
    }

    private async Task SetDllOverrideAsync(string dll, string mode, CancellationToken ct)
    {
        // Wine DLL overrides go in:
        // HKCU\Software\Wine\DllOverrides  ValueName=dll  Data=mode
        await SetRegistryKeyAsync(
            @"HKEY_CURRENT_USER\Software\Wine\DllOverrides",
            dll, mode, ct);
    }

    // ── Process runners ───────────────────────────────────────────────────────

    /// <summary>
    /// Runs a Wine built-in program (e.g. "reg", "wineboot", "winedbg").
    /// Wine built-ins are run as: wine [program] [args...]
    /// </summary>
    private async Task<bool> RunWineAsync(string program,
        string[] programArgs, CancellationToken ct)
    {
        if (WinePath is null) return false;

        var args = new List<string> { program };
        args.AddRange(programArgs);

        return await RunProcessAsync(WinePath, args, ct);
    }

    private async Task RunWineserverAsync(string[] wineserverArgs, CancellationToken ct)
    {
        // wineserver is in the same dir as wine
        string dir        = Path.GetDirectoryName(WinePath ?? "") ?? "";
        string wineserver = Path.Combine(dir, "wineserver");
        if (!File.Exists(wineserver))
        {
            // Fall back: try PATH
            wineserver = FindInPath("wineserver") ?? "wineserver";
        }
        await RunProcessAsync(wineserver, wineserverArgs.ToList(), ct);
    }

    private async Task<bool> RunWinetricksAsync(string winetricks,
        string[] args, CancellationToken ct)
    {
        var argList = new List<string>
        {
            "--unattended",
            "--no-isolate",
        };
        argList.AddRange(args);
        return await RunProcessAsync(winetricks, argList, ct);
    }

    private async Task<bool> RunProcessAsync(string exe, List<string> args,
        CancellationToken ct)
    {
        var env = WineDetector.GetWineEnvironment();

        _log.LogDebug("WineSetup: {Exe} {Args}", exe, string.Join(" ", args));
        Console.WriteLine($"[WINE-SETUP] {exe} {string.Join(" ", args)}");

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            foreach (var (k, v) in env)   psi.Environment[k] = v;

            // Remove DISPLAY on macOS (not needed, avoids XQuartz dependency)
            if (OperatingSystem.IsMacOS())
                psi.Environment.Remove("DISPLAY");

            using var p = Process.Start(psi)!;

            // Drain stdout/stderr
            _ = Task.Run(() => DrainAsync(p.StandardOutput, "[WINE-SETUP OUT]"), ct);
            _ = Task.Run(() => DrainAsync(p.StandardError,  "[WINE-SETUP ERR]"), ct);

            await p.WaitForExitAsync(ct);
            bool ok = p.ExitCode == 0;
            _log.LogDebug("WineSetup: {Exe} exited {Code}", exe, p.ExitCode);
            return ok;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "WineSetup: {Exe} failed", exe);
            return false;
        }
    }

    private async Task DrainAsync(System.IO.StreamReader reader, string prefix)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                Console.WriteLine($"{prefix} {line}");
                _log.LogTrace("{Prefix} {Line}", prefix, line);
            }
        }
        catch { }
    }

    // ── Tool finders ──────────────────────────────────────────────────────────

    private static string? FindWinetricks()
    {
        // Common locations
        foreach (var candidate in new[]
        {
            "/opt/homebrew/bin/winetricks",
            "/usr/local/bin/winetricks",
            "/usr/bin/winetricks",
        })
            if (File.Exists(candidate)) return candidate;

        return FindInPath("winetricks");
    }

    private static string? FindInPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                             .Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            string full = Path.Combine(dir, exe);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    private static void Report(IProgress<(int, string)>? p, int pct, string msg)
        => p?.Report((pct, msg));
}
