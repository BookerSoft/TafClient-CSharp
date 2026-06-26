namespace TafClient.Service;

/// <summary>
/// Writes TAForever.ini — the session-configuration file TA's own engine
/// reads at startup to learn the session name, map/mission, player limit,
/// unit cap, locked-options flag, and starting-position mode.
///
/// This is a near-identical port of GpgNetApp/Program.cs's CreateTAInitFile,
/// duplicated here rather than shared via a project reference because
/// GpgNetApp is referenced from TafClient.csproj with
/// ReferenceOutputAssembly="false" (it's a separate Exe, not a library —
/// TafClient deliberately doesn't load its assembly into its own process).
/// The logic itself is simple, self-contained file I/O with no other
/// dependencies, so duplication here is low-risk; if the template format
/// ever changes, both copies need updating together.
///
/// Used in two places now:
///   1. HostGameDialog.DoHost() — writes the file immediately when the user
///      clicks "Host Game", before the server request or any of the
///      talauncher/ICE-adapter/gpgnet4ta launch chain even starts.
///   2. GpgNetApp's HostGame event handler — kept as a safety-net second
///      write (using whatever the server's game_launch actually reports),
///      in case the dialog's earlier write used stale info or didn't happen
///      for some reason (e.g. an older client driving the same gpgnet4ta).
/// </summary>
public static class TaInitFileWriter
{
    /// <summary>
    /// Writes TAForever.ini into <paramref name="gamePath"/>. Returns true on
    /// success. Never throws — all failures are caught, logged to console,
    /// and reported via the return value so callers can decide how to react
    /// (e.g. still proceed with hosting, since a missing/stale ini is
    /// recoverable — gpgnet4ta's own write is still a fallback).
    /// </summary>
    public static bool Write(
        string gamePath, string playerName, string mission, int playerLimit,
        bool lockOptions, out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            mission ??= "";

            string? templatePath = FindTemplate();
            if (templatePath is null)
            {
                errorMessage = "taforever.ini.template not found — checked app directory and natives/bin";
                Console.WriteLine($"[TAInit] WARNING: {errorMessage}");
                return false;
            }

            Console.WriteLine($"[TAInit] Using template at: {templatePath}");
            string text = File.ReadAllText(templatePath);

            string session      = $"{playerName}'s Game";
            int    clampedLimit = Math.Max(2, Math.Min(playerLimit, 10));
            const int maxUnits  = 1000; // matches gpgnet4ta's own hardcoded value
            int    clampedUnits = Math.Max(20, Math.Min(maxUnits, 1500));

            text = text.Replace("{session}", session)
                       .Replace("{mission}", mission)
                       .Replace("{playerlimit}", clampedLimit.ToString())
                       .Replace("{maxunits}", clampedUnits.ToString())
                       .Replace("{lockoptions}", lockOptions ? "1" : "0")
                       .Replace("{location}", "2"); // random starting positions (default)

            if (!Directory.Exists(gamePath))
            {
                errorMessage = $"game path does not exist: {gamePath}";
                Console.WriteLine($"[TAInit] WARNING: {errorMessage}");
                return false;
            }

            string iniPath = Path.Combine(gamePath, "TAForever.ini");
            File.WriteAllText(iniPath, text);
            Console.WriteLine($"[TAInit] Wrote {iniPath} (session='{session}' mission='{mission}' playerLimit={clampedLimit})");
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"{ex.GetType().Name}: {ex.Message}";
            Console.WriteLine($"[TAInit] FAILED: {errorMessage}");
            return false;
        }
    }

    private static string? FindTemplate()
    {
        string direct = Path.Combine(AppContext.BaseDirectory, "taforever.ini.template");
        if (File.Exists(direct)) return direct;

        string altPath = Path.Combine(AppContext.BaseDirectory, "natives", "bin", "taforever.ini.template");
        if (File.Exists(altPath)) return altPath;

        return null;
    }
}
