namespace TafClient.Service;

/// <summary>
/// Single source of truth for where every log file lives:
/// $HOME/TAF/Logs (e.g. ~/TAF/Logs on macOS/Linux, C:\Users\you\TAF\Logs on
/// Windows, since Environment.SpecialFolder.UserProfile resolves correctly
/// on all three).
///
/// Consolidates what was previously three different locations:
///   - hostlog.txt / joinlog.txt — were siblings of the executable
///     (AppContext.BaseDirectory), which meant they lived inside a macOS
///     .app bundle's Contents/MacOS/ or wherever the Windows publish output
///     happened to be — awkward to find and easy to lose track of.
///   - talauncher_*.log / registerdplay_*.log / game_*.log — were already
///     under ~/.taf/logs (GameLaunchService.GetLogPath), a different root
///     from the above.
///   - ice-adapter.log — had NO configured location at all; faf-ice-adapter
///     defaults to a literal "LOG_DIR_IS_UNDEFINED" folder when its LOG_DIR
///     system property isn't set, which we never set.
///   - taf-client*.log (the main Serilog sink) — was under a relative
///     "logs/" folder (relative to whatever the current working directory
///     happened to be at startup — not reliable for a double-clicked .app).
/// All four now resolve under the same $HOME/TAF/Logs root.
/// </summary>
public static class LogPaths
{
    /// <summary>$HOME/TAF/Logs — created on first access if it doesn't exist.</summary>
    public static string Root
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "TAF", "Logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string HostLogPath  => Path.Combine(Root, "hostlog.txt");
    public static string JoinLogPath  => Path.Combine(Root, "joinlog.txt");

    /// <summary>Per-process log path for talauncher/registerdplay/gpgnet4ta, keyed by game uid.</summary>
    public static string ForGameProcess(string processName, int uid) =>
        Path.Combine(Root, $"{processName}_{uid}.log");

    /// <summary>
    /// Directory to hand to the ICE adapter via -DLOG_DIR=... so it writes
    /// ice-adapter.log here instead of falling back to its own
    /// "LOG_DIR_IS_UNDEFINED" default when the property isn't set.
    /// </summary>
    public static string IceAdapterLogDir => Root;

    /// <summary>Base path (without extension) for the main Serilog rolling file sink.</summary>
    public static string MainLogBasePath => Path.Combine(Root, "taf-client.log");
}
