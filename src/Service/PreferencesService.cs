using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace TafClient.Service;

// ─── Model ────────────────────────────────────────────────────────────────────

/// <summary>
/// Port of TotalAnnihilationPrefs — per-mod installation preferences.
/// One instance per KnownFeaturedMod.
/// </summary>
public class ModPreferences
{
    [JsonPropertyName("exe_path")]
    public string ExePath { get; set; } = string.Empty;

    [JsonPropertyName("cmd_options")]
    public string CmdOptions { get; set; } = string.Empty;
}

/// <summary>Root preferences object persisted to ~/.taf/preferences.json.</summary>
public class UserPreferences
{
    /// <summary>Per-mod exe path and command line options.</summary>
    [JsonPropertyName("mods")]
    public Dictionary<string, ModPreferences> Mods { get; set; } = new();

    /// <summary>Auto-login username (cleared on manual logout).</summary>
    [JsonPropertyName("auto_login_username")]
    public string? AutoLoginUsername { get; set; }

    /// <summary>Last used Wine prefix path.</summary>
    [JsonPropertyName("wine_prefix")]
    public string? WinePrefix { get; set; }

    /// <summary>
    /// Last successfully-resolved Java executable path, cached so a transient
    /// PATH/environment issue on one launch doesn't force re-discovery from
    /// scratch — and so this also still works if Java was found once via a
    /// search path that isn't always reliable (e.g. PATH varying by how the
    /// app happens to be started).
    /// </summary>
    [JsonPropertyName("java_path")]
    public string? JavaPath { get; set; }
}

// ─── Service ──────────────────────────────────────────────────────────────────

/// <summary>
/// Persists and loads user preferences from ~/.taf/preferences.json.
/// Mirrors com.faforever.client.preferences.PreferencesService.
/// </summary>
public sealed class PreferencesService
{
    private readonly ILogger<PreferencesService> _log;
    private UserPreferences _prefs = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented             = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string PrefsFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".taf", "preferences.json");

    public PreferencesService(ILogger<PreferencesService> log)
    {
        _log = log;
        Load();
    }

    // ── Public accessors ──────────────────────────────────────────────────────

    public UserPreferences Preferences => _prefs;

    /// <summary>Get mod preferences for a technical mod name, creating entry if absent.</summary>
    public ModPreferences GetMod(string modTechnical)
    {
        if (!_prefs.Mods.TryGetValue(modTechnical, out var mp))
        {
            mp = new ModPreferences();
            _prefs.Mods[modTechnical] = mp;
        }
        return mp;
    }

    /// <summary>Set exe path for a mod and save.</summary>
    public void SetModExePath(string modTechnical, string path)
    {
        GetMod(modTechnical).ExePath = path;
        Save();
    }

    /// <summary>Set command line options for a mod and save.</summary>
    public void SetModCmdOptions(string modTechnical, string opts)
    {
        GetMod(modTechnical).CmdOptions = opts;
        Save();
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    public void Load()
    {
        string path = PrefsFilePath;
        if (!File.Exists(path))
        {
            _log.LogInformation("[PREFS] No preferences file at {Path} — using defaults", path);
            return;
        }
        try
        {
            string json = File.ReadAllText(path);
            _prefs = JsonSerializer.Deserialize<UserPreferences>(json, JsonOpts)
                     ?? new UserPreferences();
            _log.LogInformation("[PREFS] Loaded from {Path} ({Count} mods)",
                path, _prefs.Mods.Count);
            foreach (var (k, v) in _prefs.Mods)
                Console.WriteLine($"[PREFS] mod={k} exe={v.ExePath}");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[PREFS] Failed to load preferences from {Path}", path);
            _prefs = new UserPreferences();
        }
    }

    public void Save()
    {
        string path = PrefsFilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = JsonSerializer.Serialize(_prefs, JsonOpts);
            File.WriteAllText(path, json);
            _log.LogDebug("[PREFS] Saved to {Path}", path);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[PREFS] Failed to save preferences to {Path}", path);
        }
    }
}
