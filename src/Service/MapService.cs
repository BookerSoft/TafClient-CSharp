using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TafClient.Config;

namespace TafClient.Service;

// ─── Domain ───────────────────────────────────────────────────────────────────

/// <summary>Port of MapBean — key fields used for install-check and download.</summary>
public class MapBean
{
    public string MapName        { get; set; } = string.Empty;  // e.g. "SHERWOOD"
    public string HpiArchiveName { get; set; } = string.Empty;  // e.g. "SHERWOOD.ufo"
    public string Crc            { get; set; } = string.Empty;  // hex CRC
    public string? DisplayName   { get; set; }
    public string? Author        { get; set; }
    public string? Description   { get; set; }
    public int    MaxPlayers     { get; set; }
    public int    Width          { get; set; }
    public int    Height         { get; set; }
    public int    SeaLevel       { get; set; }
    public int    Downloads      { get; set; }
    public bool   Ranked         { get; set; }
    public Uri?   DownloadUrl    { get; set; }  // direct download URL from API
    public Uri?   ThumbnailUrl   { get; set; }  // mirrors MapBean.thumbnailUrl (mini preview)
    public bool   IsInstalled    { get; set; }
}

// ─── API response shapes ──────────────────────────────────────────────────────
// /data/map?include=latestVersion,author&filter=latestVersion.hidden=="false"

internal class ApiMapPage
{
    [JsonPropertyName("data")]  public List<ApiMapData>? Data  { get; set; }
    [JsonPropertyName("meta")]  public ApiMeta?         Meta  { get; set; }
}
internal class ApiMeta
{
    [JsonPropertyName("page")] public ApiPageMeta? Page { get; set; }
}
internal class ApiPageMeta
{
    [JsonPropertyName("totalRecords")] public int TotalRecords { get; set; }
}
internal class ApiMapData
{
    [JsonPropertyName("id")]            public string Id         { get; set; } = "";
    [JsonPropertyName("attributes")]    public ApiMapAttrs? Attrs { get; set; }
    [JsonPropertyName("relationships")] public ApiMapRels?  Rels  { get; set; }
}
internal class ApiMapAttrs
{
    [JsonPropertyName("displayName")]   public string? DisplayName  { get; set; }
    [JsonPropertyName("description")]   public string? Description  { get; set; }
    [JsonPropertyName("gamesPlayed")]   public int     GamesPlayed  { get; set; }
}
internal class ApiMapRels
{
    [JsonPropertyName("latestVersion")] public ApiRelData? LatestVersion { get; set; }
    [JsonPropertyName("author")]        public ApiRelData? Author        { get; set; }
}
internal class ApiRelData
{
    [JsonPropertyName("data")] public ApiRelId? Data { get; set; }
}
internal class ApiRelId
{
    [JsonPropertyName("id")]   public string Id   { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
}
internal class ApiIncluded
{
    [JsonPropertyName("id")]         public string Id    { get; set; } = "";
    [JsonPropertyName("type")]       public string Type  { get; set; } = "";
    [JsonPropertyName("attributes")] public ApiVersionAttrs? Attrs { get; set; }
}
internal class ApiVersionAttrs
{
    [JsonPropertyName("archiveName")]  public string? ArchiveName  { get; set; }
    [JsonPropertyName("crc")]          public string? Crc          { get; set; }
    [JsonPropertyName("maxPlayers")]   public int     MaxPlayers   { get; set; }
    [JsonPropertyName("width")]        public int     Width        { get; set; }
    [JsonPropertyName("height")]       public int     Height       { get; set; }
    [JsonPropertyName("ranked")]       public bool    Ranked       { get; set; }
    [JsonPropertyName("downloadUrl")]  public string? DownloadUrl  { get; set; }
    [JsonPropertyName("thumbnailUrl")] public string? ThumbnailUrl { get; set; }  // direct from API — authoritative
    [JsonPropertyName("login")]        public string? Login        { get; set; }  // author
}

// ─── Service ──────────────────────────────────────────────────────────────────

/// <summary>
/// Port of com.faforever.client.map.MapService.
///
/// Responsibilities implemented here:
///   • isInstalled() — check if an HPI archive exists in the mod's map folder
///   • ensureMap()   — auto-download if not installed (mirrors optionalEnsureMap)
///   • searchMaps()  — query /data/map API (mirrors findByQueryWithPageCount)
///   • downloadAndInstall() — HTTP download → place in map folder
/// </summary>
public class MapService
{
    // Download URL format mirrors mapDownloadUrlFormat from ClientProperties.Vault
    private const string DownloadUrlFormat = "https://content.taforever.com/maps/{0}";
    // Preview URL format mirrors mapPreviewUrlFormat
    private const string PreviewUrlFormat  = "https://content.taforever.com/maps/previews/{0}/{1}.png";
    private const string ApiBase           = "https://api.taforever.com";

    public static Uri GetThumbnailUrl(string mapName) =>
        new(string.Format(PreviewUrlFormat, "mini", Uri.EscapeDataString(mapName)));

    private readonly ILogger<MapService>  _log;
    private readonly ClientProperties    _props;
    private readonly HttpClient          _http;
    private readonly PreferencesService? _prefs;
    private readonly MapDatabase         _db;

    // ── Per-mod map cache (mirrors Installation.maps ObservableList) ──────────
    // Populated from SQLite on startup (instant), refreshed by background scans.
    private readonly ConcurrentDictionary<string, List<MapBean>> _mapCache = new();
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new();

    /// <summary>Mods that have already been scanned once this session — guards against
    /// redundant re-scans from dropdown selection, watcher debounce, etc.</summary>
    private readonly ConcurrentDictionary<string, bool> _scannedOnce = new();

    // Guard against concurrent downloads
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _inProgress = new();

    /// <summary>Raised when a mod's installed map list is refreshed (on background thread).</summary>
    public event Action<string>? MapsRefreshed;   // arg = modTechnical

    // Known mod technical names — mirrors KnownFeaturedMod enum
    private static readonly string[] KnownMods =
        ["tacc", "taesc", "tazero", "tamayhem", "tavmod", "tatw", "coop", "ladder1v1"];

    public MapService(ILogger<MapService> log, IOptions<ClientProperties> props,
                      HttpClient http, PreferencesService prefs, MapDatabase db)
    {
        _log   = log;
        _props = props.Value;
        _http  = http;
        _prefs = prefs;
        _db    = db;

        // Pre-load from SQLite so GetInstalledMaps has data immediately at startup,
        // even before the background directory scan completes.
        foreach (var mod in KnownMods)
        {
            var cached = _db.LoadMaps(mod);
            if (cached.Count > 0)
            {
                _mapCache[mod] = cached;
                Console.WriteLine($"[MAPS] Loaded {cached.Count} cached maps for {mod} from SQLite");
            }
        }
    }

    // ── Startup scan ──────────────────────────────────────────────────────────

    /// <summary>
    /// Mirrors MapService.afterPropertiesSet() — scans installed maps for all configured mods
    /// on a background thread. Call once from TafApp after login.
    /// Also registers FileSystemWatchers to re-scan when archives are added/removed,
    /// mirroring startDirectoryWatcher(). Each mod is only ever fully scanned once per
    /// session via this path — subsequent calls elsewhere (dropdown selection, etc.)
    /// reuse the cached/SQLite result unless a real archive change is detected or the
    /// user explicitly clicks Scan.
    /// </summary>
    public void ScanAllModsInBackground()
    {
        foreach (var mod in KnownMods)
        {
            string? gameRoot = GetMapsFolder(mod);
            if (gameRoot is null || !Directory.Exists(gameRoot)) continue;
            if (_scannedOnce.TryAdd(mod, true))
                _ = Task.Run(() => ScanModAsync(mod, gameRoot));
            StartWatcher(mod, gameRoot);
        }
    }

    /// <summary>
    /// Re-scan a single mod. By default this is a no-op if the mod has already been
    /// scanned once this session (e.g. from app startup or a prior dropdown selection) —
    /// pass force=true (e.g. from the Scan button) to always re-scan.
    /// </summary>
    public void RefreshMod(string modTechnical, bool force = false)
    {
        if (!force && _scannedOnce.ContainsKey(modTechnical))
        {
            Console.WriteLine($"[MAPS] {modTechnical} already scanned this session — skipping (use force to re-scan)");
            return;
        }
        // If maps.db already had cached data for this mod (loaded in the
        // constructor — see the comment there), skip the filesystem
        // rescan entirely. This is the actual fix for "we don't need to
        // rescan every launch": the DB-backed instant-availability already
        // worked, but the background rescan was still always triggered
        // afterward regardless, doing real disk I/O across every map
        // archive every single launch even when the cached data was
        // already good. force=true still always re-scans (used correctly
        // by Install Mod and other explicit-refresh paths that genuinely
        // need fresh data).
        if (!force && _mapCache.TryGetValue(modTechnical, out var cachedOnLaunch) && cachedOnLaunch.Count > 0)
        {
            Console.WriteLine($"[MAPS] {modTechnical} already has {cachedOnLaunch.Count} maps loaded from maps.db — skipping filesystem rescan (use force to re-scan)");
            _scannedOnce[modTechnical] = true;
            string? watcherRoot = GetMapsFolder(modTechnical);
            if (watcherRoot is not null && Directory.Exists(watcherRoot))
                StartWatcher(modTechnical, watcherRoot); // still watch for future changes, just don't do the expensive initial scan
            return;
        }
        string? gameRoot = GetMapsFolder(modTechnical);
        if (gameRoot is null || !Directory.Exists(gameRoot)) return;
        _scannedOnce[modTechnical] = true;
        _ = Task.Run(() => ScanModAsync(modTechnical, gameRoot));
        StartWatcher(modTechnical, gameRoot);
    }

    /// <summary>
    /// Awaitable version of RefreshMod, for CLI/headless callers that need to
    /// know when the scan (and SQLite write) has actually completed. Same
    /// once-per-session guard as RefreshMod — pass force=true to override.
    /// </summary>
    public async Task RefreshModAsync(string modTechnical, bool force = false)
    {
        if (!force && _scannedOnce.ContainsKey(modTechnical))
        {
            Console.WriteLine($"[MAPS] {modTechnical} already scanned this session — skipping (use force to re-scan)");
            return;
        }
        // Same DB-cache fast path as RefreshMod above.
        if (!force && _mapCache.TryGetValue(modTechnical, out var cachedOnLaunchAsync) && cachedOnLaunchAsync.Count > 0)
        {
            Console.WriteLine($"[MAPS] {modTechnical} already has {cachedOnLaunchAsync.Count} maps loaded from maps.db — skipping filesystem rescan (use force to re-scan)");
            _scannedOnce[modTechnical] = true;
            string? watcherRoot = GetMapsFolder(modTechnical);
            if (watcherRoot is not null && Directory.Exists(watcherRoot))
                StartWatcher(modTechnical, watcherRoot);
            return;
        }
        string? gameRoot = GetMapsFolder(modTechnical);
        if (gameRoot is null || !Directory.Exists(gameRoot))
        {
            Console.WriteLine($"[MAPS] No valid game path found for {modTechnical}, skipping");
            return;
        }
        _scannedOnce[modTechnical] = true;
        await ScanModAsync(modTechnical, gameRoot);
        StartWatcher(modTechnical, gameRoot);
    }

    private async Task ScanModAsync(string mod, string gameRoot)
    {
        _log.LogInformation("[MAPS] Background scan start: mod={Mod} dir={Dir}", mod, gameRoot);
        Console.WriteLine($"[MAPS] Scanning mod={mod} dir={gameRoot}");
        var maps = await Task.Run(() => ScanDirectory(mod, gameRoot));
        _mapCache[mod] = maps;
        _db.ReplaceMaps(mod, maps);   // persist to SQLite — table name "maps_{mod}"
        _log.LogInformation("[MAPS] Scan complete: mod={Mod} found={Count}", mod, maps.Count);
        Console.WriteLine($"[MAPS] Scan complete: mod={mod} found={maps.Count} maps (saved to SQLite)");
        MapsRefreshed?.Invoke(mod);
    }

    private void StartWatcher(string mod, string gameRoot)
    {
        // Stop previous watcher for this mod
        if (_watchers.TryRemove(mod, out var old))
            try { old.Dispose(); } catch { }

        try
        {
            var watcher = new FileSystemWatcher(gameRoot)
            {
                Filter                = "*.*",
                NotifyFilter          = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents   = true,
            };

            void OnChanged(object _, FileSystemEventArgs e)
            {
                string ext = Path.GetExtension(e.Name ?? "").ToLowerInvariant();
                if (ext is ".ufo" or ".hpi" or ".ccx" or ".gp3")
                {
                    _log.LogInformation("[MAPS] Dir change detected, re-scanning mod={Mod}", mod);
                    _ = Task.Run(() => ScanModAsync(mod, gameRoot));
                }
            }

            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Changed += OnChanged;
            _watchers[mod] = watcher;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[MAPS] Could not start file watcher for {Dir}", gameRoot);
        }
    }

    // ── Install check ─────────────────────────────────────────────────────────

    /// <summary>
    /// Mirrors MapService.isInstalled(modTechnical, mapName, mapCrc).
    /// Checks whether the HPI archive for the given map is present in the
    /// mod's installation folder (from TotalAnnihilationPrefs.installedPath).
    /// </summary>
    public bool IsInstalled(string modTechnical, string hpiArchiveName)
    {
        string? folder = GetMapsFolder(modTechnical);
        if (folder is null) return false;
        bool exists = File.Exists(Path.Combine(folder, hpiArchiveName));
        _log.LogDebug("IsInstalled {Mod}/{Archive} => {Result}", modTechnical, hpiArchiveName, exists);
        return exists;
    }

    // ── Auto-download on join (mirrors optionalEnsureMap) ─────────────────────

    /// <summary>
    /// Mirrors MapService.optionalEnsureMap() called by GameService before launch.
    /// Downloads the map HPI archive if not already installed.
    /// Returns true if the map is available (either pre-existing or just downloaded).
    /// </summary>
    public async Task<bool> EnsureMapAsync(
        string modTechnical,
        string mapName,
        string? mapCrc,
        string? hpiArchiveName,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(hpiArchiveName))
        {
            _log.LogWarning("EnsureMap: no HPI archive name for map {Map}", mapName);
            return false;
        }

        if (IsInstalled(modTechnical, hpiArchiveName))
        {
            _log.LogDebug("Map already installed: {Archive}", hpiArchiveName);
            return true;
        }

        _log.LogInformation("Map {Archive} not installed — downloading for {Mod}", hpiArchiveName, modTechnical);
        return await DownloadAndInstallAsync(modTechnical, hpiArchiveName, progress, ct);
    }

    // ── Download ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Mirrors MapService.downloadAndInstallArchive().
    /// Downloads the HPI file from the CDN and writes it to the mod's maps folder.
    /// Deduplicates concurrent download attempts for the same archive.
    /// </summary>
    public async Task<bool> DownloadAndInstallAsync(
        string modTechnical,
        string hpiArchiveName,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        string key = $"{modTechnical}/{hpiArchiveName}";

        // Dedup: if already downloading, wait for it
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_inProgress.TryAdd(key, tcs))
        {
            _log.LogInformation("Already downloading {Archive}, waiting…", hpiArchiveName);
            return await _inProgress[key].Task;
        }

        try
        {
            string? mapsFolder = GetMapsFolder(modTechnical);
            if (mapsFolder is null)
            {
                _log.LogWarning("No maps folder configured for mod {Mod}", modTechnical);
                tcs.SetResult(false);
                return false;
            }

            Directory.CreateDirectory(mapsFolder);

            string url     = string.Format(DownloadUrlFormat, Uri.EscapeDataString(hpiArchiveName));
            string destPath = Path.Combine(mapsFolder, hpiArchiveName);
            string tmpPath  = destPath + ".tmp";

            _log.LogInformation("Downloading {Url} → {Dest}", url, destPath);

            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long? total = response.Content.Headers.ContentLength;
            await using var src  = await response.Content.ReadAsStreamAsync(ct);
            await using var dest = File.Create(tmpPath);

            var buf      = new byte[81920];
            long written = 0;
            int  read;
            while ((read = await src.ReadAsync(buf, ct)) > 0)
            {
                await dest.WriteAsync(buf.AsMemory(0, read), ct);
                written += read;
                if (total > 0) progress?.Report((double)written / total.Value);
            }

            await dest.FlushAsync(ct);
            dest.Close();

            // Atomic rename
            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(tmpPath, destPath);

            _log.LogInformation("Downloaded {Archive} ({Bytes} bytes)", hpiArchiveName, written);

            // Trigger an immediate re-scan so the new map is indexed right away —
            // don't rely solely on the FileSystemWatcher, which can debounce/coalesce
            // rapid file events and may lag behind a UI that wants instant feedback.
            _ = Task.Run(() => ScanModAsync(modTechnical, mapsFolder));

            tcs.SetResult(true);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to download {Archive}", hpiArchiveName);
            tcs.SetResult(false);
            return false;
        }
        finally
        {
            _inProgress.TryRemove(key, out _);
        }
    }

    // ── Map vault search ──────────────────────────────────────────────────────

    /// <summary>
    /// Mirrors MapService.findByQueryWithPageCount().
    /// Queries /data/map with display name filter and pagination.
    /// </summary>
    public async Task<(List<MapBean> Maps, int TotalCount)> SearchMapsAsync(
        string? nameFilter = null,
        int page     = 1,
        int pageSize = 50,
        string? sortBy  = "latestVersion.createTime",
        bool sortDesc   = true,
        CancellationToken ct = default)
    {
        // Build RSQL filter — mirrors NOT_HIDDEN + optional name filter
        var filters = new List<string> { "latestVersion.hidden==\"false\"" };
        if (!string.IsNullOrWhiteSpace(nameFilter))
            filters.Add($"displayName==\"*{Uri.EscapeDataString(nameFilter)}*\"");
        string filter = string.Join(";", filters);

        string sort = sortDesc ? $"-{sortBy}" : sortBy!;

        string url = $"{ApiBase}/data/map" +
                     $"?include=latestVersion,author" +
                     $"&filter={filter}" +
                     $"&sort={sort}" +
                     $"&page[number]={page}&page[size]={pageSize}";

        _log.LogDebug("Map search: {Url}", url);

        try
        {
            using var req  = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Accept", "application/vnd.api+json");
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            string json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Parse included resources (latestVersion, author)
            var included = new Dictionary<string, ApiVersionAttrs>();
            if (root.TryGetProperty("included", out var incArr))
            {
                foreach (var inc in incArr.EnumerateArray())
                {
                    string incId   = inc.GetProperty("id").GetString() ?? "";
                    string incType = inc.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                    if (incType is "mapVersion" or "player" && inc.TryGetProperty("attributes", out var attrs))
                    {
                        included[incId] = JsonSerializer.Deserialize<ApiVersionAttrs>(attrs.GetRawText())
                                          ?? new ApiVersionAttrs();
                        included[incId + ":type"] = new ApiVersionAttrs { Login = incType };
                    }
                }
            }

            var maps = new List<MapBean>();
            if (root.TryGetProperty("data", out var dataArr))
            {
                foreach (var d in dataArr.EnumerateArray())
                {
                    var bean = new MapBean();
                    if (d.TryGetProperty("attributes", out var a))
                    {
                        bean.DisplayName = a.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
                        bean.Description = a.TryGetProperty("description",  out var desc) ? desc.GetString() : null;
                        bean.Downloads   = a.TryGetProperty("gamesPlayed",  out var gp) ? gp.GetInt32() : 0;
                    }

                    // Get latestVersion id from relationships
                    if (d.TryGetProperty("relationships", out var rels) &&
                        rels.TryGetProperty("latestVersion", out var lvRel) &&
                        lvRel.TryGetProperty("data", out var lvData))
                    {
                        string lvId = lvData.TryGetProperty("id", out var lid) ? lid.GetString() ?? "" : "";
                        if (included.TryGetValue(lvId, out var lv))
                        {
                            bean.HpiArchiveName = lv.ArchiveName ?? "";
                            bean.Crc            = lv.Crc        ?? "";
                            bean.MaxPlayers     = lv.MaxPlayers;
                            bean.Width          = lv.Width;
                            bean.Height         = lv.Height;
                            bean.Ranked         = lv.Ranked;
                            if (lv.DownloadUrl != null && Uri.TryCreate(lv.DownloadUrl, UriKind.Absolute, out var dlUri))
                                bean.DownloadUrl = dlUri;
                            // Use the API-provided thumbnailUrl — it is the authoritative URL.
                            // mirrors MapVersion.getThumbnailUrl() used in MapBean.of(MapVersion)
                            if (lv.ThumbnailUrl != null && Uri.TryCreate(lv.ThumbnailUrl, UriKind.Absolute, out var thumbUri))
                                bean.ThumbnailUrl = thumbUri;
                        }
                    }

                    // MapName = archive name without extension, or displayName
                    bean.MapName = !string.IsNullOrEmpty(bean.HpiArchiveName)
                        ? Path.GetFileNameWithoutExtension(bean.HpiArchiveName)
                        : bean.DisplayName ?? "";

                    // Only construct fallback URL if the API didn't provide one
                    if (bean.ThumbnailUrl is null && !string.IsNullOrEmpty(bean.MapName))
                        bean.ThumbnailUrl = GetThumbnailUrl(bean.MapName);

                    maps.Add(bean);
                }
            }

            int total = 0;
            if (root.TryGetProperty("meta", out var meta) &&
                meta.TryGetProperty("page",  out var pg) &&
                pg.TryGetProperty("totalRecords", out var tr))
                total = tr.GetInt32();

            return (maps, total);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Map search failed");
            return ([], 0);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the game installation root for a mod.
    /// Mirrors TotalAnnihilationPrefs.getInstalledPath() — the directory containing TotalA.exe.
    /// TA maps (HPI/UFO/CCX files) live in this directory alongside the executable.
    ///
    /// Priority:
    ///   1. TAF_GAME_PATH_{MOD} env var  (e.g. TAF_GAME_PATH_TAESC)
    ///   2. TAF_GAME_PATH env var
    ///   3. TAF_MAPS_DIR env var  (legacy override)
    ///   4. ~/TotalAnnihilation/{mod}  (default install convention)
    /// </summary>
    public string? GetMapsFolder(string modTechnical)
    {
        // 1. User-set path from Settings tab (if configured and valid)
        string savedPath = _prefs?.GetMod(modTechnical).ExePath ?? string.Empty;
        if (!string.IsNullOrEmpty(savedPath))
        {
            string gameRoot = Directory.Exists(savedPath)
                ? savedPath
                : Path.GetDirectoryName(savedPath) ?? savedPath;

            if (Directory.Exists(gameRoot))
            {
                Console.WriteLine($"[MAPS] Using user-set path for {modTechnical}: {gameRoot}");
                return gameRoot;
            }
            Console.WriteLine($"[MAPS] User-set path for {modTechnical} invalid, falling back: {gameRoot}");
        }

        // 2. Env vars (advanced/dev override)
        string? fromMod = Environment.GetEnvironmentVariable($"TAF_GAME_PATH_{modTechnical.ToUpper()}");
        if (!string.IsNullOrEmpty(fromMod))
        {
            Console.WriteLine($"[MAPS] TAF_GAME_PATH_{modTechnical.ToUpper()} = {fromMod}");
            return fromMod;
        }

        string? fromBase = Environment.GetEnvironmentVariable("TAF_GAME_PATH");
        if (!string.IsNullOrEmpty(fromBase))
        {
            Console.WriteLine($"[MAPS] TAF_GAME_PATH = {fromBase}");
            return fromBase;
        }

        string? envDir = Environment.GetEnvironmentVariable("TAF_MAPS_DIR");
        if (!string.IsNullOrEmpty(envDir))
        {
            Console.WriteLine($"[MAPS] TAF_MAPS_DIR = {envDir}");
            return envDir;
        }

        // 3. Default install path: ~/TotalAnnihilation/{mod}
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string def  = Path.Combine(home, "TotalAnnihilation", modTechnical);
        Console.WriteLine($"[MAPS] default path for {modTechnical} = {def} (exists={Directory.Exists(def)})");
        return def;
    }

    /// <summary>
    /// Downloads thumbnail bytes for a map using the API-provided URL (authoritative)
    /// or falling back to the constructed mini-preview URL.
    /// </summary>
    public async Task<byte[]?> GetThumbnailBytesAsync(string mapName, CancellationToken ct = default)
        => await FetchBytesAsync(GetThumbnailUrl(mapName), ct);

    public async Task<byte[]?> GetThumbnailBytesAsync(MapBean map, CancellationToken ct = default)
    {
        // Use API-provided URL first — it's authoritative and works even when
        // our constructed URL would get the name wrong (e.g. maps with special chars)
        var url = map.ThumbnailUrl ?? (string.IsNullOrEmpty(map.MapName) ? null : GetThumbnailUrl(map.MapName));
        if (url is null) return null;
        return await FetchBytesAsync(url, ct);
    }

    private async Task<byte[]?> FetchBytesAsync(Uri url, CancellationToken ct)
    {
        try
        {
            var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogDebug("Thumbnail HTTP {Status} for {Url}", (int)response.StatusCode, url);
                return null;
            }
            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Thumbnail fetch failed for {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Returns the cached installed maps for a mod.
    /// Returns the cache immediately (never blocks) — call ScanAllModsInBackground() at startup.
    /// Mirrors Installation.maps observable list.
    /// </summary>
    public IReadOnlyList<MapBean> GetInstalledMaps(string modTechnical)
    {
        if (_mapCache.TryGetValue(modTechnical, out var cached) && cached.Count > 0)
            return cached;

        // Cache miss — trigger a background scan and return empty for now
        string? gameRoot = GetMapsFolder(modTechnical);
        if (gameRoot is not null && Directory.Exists(gameRoot) && _scannedOnce.TryAdd(modTechnical, true))
        {
            Console.WriteLine($"[MAPS] Cache miss for {modTechnical}, triggering background scan");
            _ = Task.Run(() => ScanModAsync(modTechnical, gameRoot));
        }
        return [];
    }

    /// <summary>
    /// Performs the actual directory scan: runs maptool --outputformat delimited
    /// and filters out archives that contain no OTA/TNT map pairs.
    /// Falls back to file extension scan if maptool is unavailable.
    /// Mirrors MapTool.listMapsInstalled(gamePath, cacheDir).
    /// </summary>
    private List<MapBean> ScanDirectory(string modTechnical, string gameRoot)
    {
        // Use the C# MapTool logic directly in-process — no native binary needed.
        // Mirrors MapTool/Program.cs CollectMapsFromArchive() exactly.
        // Scans all archive files in the game root; archives with no OTA/TNT pairs
        // produce zero MapBean entries and are therefore excluded automatically.
        var archives = Directory.EnumerateFiles(gameRoot)
            .Where(f =>
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                return ext is ".hpi" or ".ufo" or ".ccx" or ".gp3";
            })
            .ToList();

        Console.WriteLine($"[MAPS] Scanning {archives.Count} archives in {gameRoot}");

        if (archives.Count == 0)
        {
            // Help diagnose: show what's actually in the directory so it's obvious
            // whether this is a wrong-path problem or a genuinely empty install dir.
            try
            {
                var allFiles = Directory.GetFiles(gameRoot);
                Console.WriteLine($"[MAPS] WARNING: 0 archive files (.hpi/.ufo/.ccx/.gp3) found.");
                Console.WriteLine($"[MAPS] Directory contains {allFiles.Length} total file(s):");
                foreach (var f in allFiles.Take(20))
                    Console.WriteLine($"[MAPS]   {Path.GetFileName(f)}");
                if (allFiles.Length > 20)
                    Console.WriteLine($"[MAPS]   ... and {allFiles.Length - 20} more");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MAPS] Could not list directory contents: {ex.Message}");
            }
        }

        var allMaps = new List<MapBean>();

        foreach (var archivePath in archives)
        {
            string archiveName = Path.GetFileName(archivePath);
            try
            {
                var infos = ScanArchive(archivePath);
                foreach (var info in infos)
                {
                    allMaps.Add(new MapBean
                    {
                        MapName        = info.Name,
                        HpiArchiveName = archiveName,
                        Description    = info.Description,
                        Author         = info.Author,
                        MaxPlayers     = info.MaxPlayers,
                        Width          = info.Width,
                        Height         = info.Height,
                        SeaLevel       = info.SeaLevel,
                        IsInstalled    = true,
                        ThumbnailUrl   = GetThumbnailUrl(info.Name),
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MAPS] EXCEPTION scanning {archiveName}: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"[MAPS]   {ex.StackTrace}");
                _log.LogDebug(ex, "[MAPS] Could not scan {Archive}", archiveName);
            }
        }

        _log.LogInformation("[MAPS] Scanned {Archives} archives, found {Maps} maps in {Dir}",
            archives.Count, allMaps.Count, gameRoot);
        Console.WriteLine($"[MAPS] Found {allMaps.Count} maps across {archives.Count} archives");

        if (archives.Count > 0 && allMaps.Count == 0)
        {
            Console.WriteLine("[MAPS] WARNING: archives were found and opened, but NONE produced any " +
                               "maps. This usually means either: (1) the HPI directory parser is not " +
                               "finding any .ota/.tnt files inside the archives — check the per-archive " +
                               "'root entries=' and 'found N OTA' log lines above; or (2) the OTA files " +
                               "were extracted but the TDF parser found 0 blocks in every one.");
        }

        return allMaps;
    }

    /// <summary>
    /// Scans a single HPI/UFO/CCX archive and returns MapInfo for every OTA+TNT pair found.
    /// Mirrors MapTool/Program.cs CollectMapsFromArchive() + ParseMap().
    /// Archives with no OTA/TNT pairs (graphics packs, sound packs, etc.) return empty list.
    /// </summary>
    private static List<TafToolbox.MapTool.MapInfo> ScanArchive(string archivePath)
    {
        using var fs      = File.OpenRead(archivePath);
        var       archive = new TafToolbox.Hpi.HpiArchive(fs);
        var       otaFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var       tntFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"[MAPS] ScanArchive: {Path.GetFileName(archivePath)} root entries={archive.Root.Entries.Count}");
        CollectArchiveFiles(archive, archive.Root, otaFiles, tntFiles, "");
        Console.WriteLine($"[MAPS] ScanArchive: {Path.GetFileName(archivePath)} found {otaFiles.Count} OTA, {tntFiles.Count} TNT files");

        var results = new List<TafToolbox.MapTool.MapInfo>();
        foreach (var (baseName, otaData) in otaFiles)
        {
            tntFiles.TryGetValue(baseName, out byte[]? tntData);
            var info = ParseMapInfo(baseName, otaData, tntData);
            if (info != null) results.Add(info);
        }
        return results;
    }

    private static void CollectArchiveFiles(
        TafToolbox.Hpi.HpiArchive            archive,
        TafToolbox.Hpi.HpiArchive.DirectoryEntry dir,
        Dictionary<string, byte[]>           otaFiles,
        Dictionary<string, byte[]>           tntFiles,
        string                               path)
    {
        foreach (var entry in dir.Entries)
        {
            string fullPath = string.IsNullOrEmpty(path) ? entry.Name : $"{path}/{entry.Name}";

            if (entry.File != null)
            {
                string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                Console.WriteLine($"[MAPS]   file: {fullPath} (size={entry.File.Size}, compression={entry.File.CompressionScheme})");
                if (ext is ".ota" or ".tnt")
                {
                    try
                    {
                        byte[] buf = new byte[entry.File.Size];
                        archive.Extract(entry.File, buf);
                        string baseName = Path.GetFileNameWithoutExtension(entry.Name);
                        if (ext == ".ota") otaFiles[baseName] = buf;
                        else               tntFiles[baseName] = buf;
                        Console.WriteLine($"[MAPS]     extracted OK: {fullPath} -> {buf.Length} bytes");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[MAPS]     EXTRACT FAILED: {fullPath}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            else if (entry.Directory != null)
            {
                Console.WriteLine($"[MAPS]   dir: {fullPath}/ ({entry.Directory.Entries.Count} entries)");
                CollectArchiveFiles(archive, entry.Directory, otaFiles, tntFiles, fullPath);
            }
        }
    }

    /// <summary>
    /// Parses the OTA "numplayers" field, which in real map files appears in several
    /// formats: a plain integer ("8"), a range ("2-10"), or a comma-separated list of
    /// valid player counts ("2, 4, 6, 8"). Returns the maximum player count found.
    /// </summary>
    /// <summary>
    /// Recursively searches child blocks of a TdfBlock for a key.
    /// Used when map OTA files store missionname/numplayers inside a nested block
    /// (e.g. [Schema 0]) rather than directly under [GlobalHeader].
    /// </summary>
    private static string? SearchChildren(TafToolbox.MapTool.TdfBlock block, string key)
    {
        foreach (var child in block.Children)
        {
            var val = child.Get(key);
            if (val != null) return val;
            val = SearchChildren(child, key);
            if (val != null) return val;
        }
        return null;
    }

    private static int ParseMaxPlayers(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        var numbers = System.Text.RegularExpressions.Regex.Matches(raw, @"\d+")
            .Select(m => int.Parse(m.Value))
            .ToList();
        return numbers.Count > 0 ? numbers.Max() : 0;
    }

    private static TafToolbox.MapTool.MapInfo? ParseMapInfo(
        string name, byte[] otaData, byte[]? tntData)
    {
        try
        {
            // Latin1 (codepage 1252-ish) instead of strict ASCII — OTA files can contain
            // high-bit bytes in description text; ASCII.GetString silently replaces them
            // with '?' which is harmless for parsing keys but Latin1 avoids any data loss.
            string text   = System.Text.Encoding.Latin1.GetString(otaData);
            var    blocks = TafToolbox.MapTool.TdfParser.Parse(text);

            Console.WriteLine($"[MAPS] ParseMapInfo({name}): otaBytes={otaData.Length} rootBlocks={blocks.Count}");
            foreach (var b in blocks)
                Console.WriteLine($"[MAPS]   root block: '{b.Name}' ({b.Values.Count} keys, {b.Children.Count} children)");

            TafToolbox.MapTool.TdfBlock? header = null;
            foreach (var b in blocks)
            {
                if (string.Equals(b.Name, "GlobalHeader", StringComparison.OrdinalIgnoreCase))
                { header = b; break; }
                // GlobalHeader is sometimes nested one level down (e.g. under a
                // wrapping block named after the file, or under "[Schema 0]")
                var child = b.GetChild("GlobalHeader");
                if (child != null) { header = child; break; }
            }
            if (header == null && blocks.Count > 0) header = blocks[0];
            if (header == null)
            {
                Console.WriteLine($"[MAPS] ParseMapInfo({name}): no blocks found in OTA — cannot index this map");
                return null;
            }

            int width = 0, height = 0, seaLevel = 0;
            if (tntData != null)
            {
                try
                {
                    var tnt = TafToolbox.MapTool.TntReader.Read(tntData);
                    width = tnt.Width; height = tnt.Height; seaLevel = tnt.SeaLevel;
                }
                catch (Exception tntEx)
                {
                    Console.WriteLine($"[MAPS] ParseMapInfo({name}): TNT parse failed (non-fatal): {tntEx.Message}");
                }
            }

            // Map name comes from the archive's own filename (the .ota/.tnt base name
            // inside the HPI), not from parsed OTA text — OTA "missionname" fields are
            // inconsistent across map packs (missing, nested differently, etc.) and the
            // archive filename is what TA itself uses to identify the map on disk.
            string mapName = name;

            string description = header.Get("missiondescription")
                              ?? header.Get("missiondesc")
                              ?? SearchChildren(header, "missiondescription")
                              ?? SearchChildren(header, "missiondesc")
                              ?? "";

            string numPlayersRaw = header.Get("numplayers")
                                ?? SearchChildren(header, "numplayers")
                                ?? "";

            Console.WriteLine($"[MAPS] ParseMapInfo({name}): mapName='{mapName}' numplayers='{numPlayersRaw}'");

            return new TafToolbox.MapTool.MapInfo
            {
                Name        = mapName,
                Description = description,
                Author      = header.Get("author") ?? SearchChildren(header, "author") ?? "",
                MaxPlayers  = ParseMaxPlayers(numPlayersRaw),
                Width       = width,
                Height      = height,
                SeaLevel    = seaLevel,
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MAPS] ParseMapInfo({name}): EXCEPTION {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string? FindMapTool() => null; // native binary not used; scanning is in-process

    /// <summary>
    /// Scans the game root for archive files and builds a lookup:
    /// lowercase map name → archive filename.
    /// The map name is derived from the archive's base name (without extension),
    /// which TA uses as the map identifier.
    /// </summary>
}

