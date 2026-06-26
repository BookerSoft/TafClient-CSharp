using SFML.Graphics;
using TafClient.Service;
using TafClient.UI;

namespace TafClient.UI.Widgets;

/// <summary>
/// Port of com.faforever.client.map.MapVaultController.
///
/// Layout (with preview pane):
///   Top bar:  [search box] [Sort: combo] [Search btn]
///   Left 68%: ListView of results
///   Right 32%: Preview image + map detail labels + Download button
/// </summary>
public sealed class MapSearchWidget
{
    private readonly MapService    _mapService;
    private readonly UiThreadQueue _uiQ;

    private TGUI.ListView?    _list;
    private TGUI.EditBox?     _searchBox;
    private TGUI.ComboBox?    _sortCombo;
    private TGUI.ComboBox?    _modCombo;
    private TGUI.Label?       _modStatusLabel;
    private TGUI.Button?      _scanBtn;
    private TGUI.Button?      _searchBtn;
    private TGUI.Button?      _downloadBtn;
    private TGUI.Label?       _statusLabel;
    private TGUI.ProgressBar? _progressBar;

    // Preview pane widgets
    private TGUI.Panel?   _previewPanel;
    private TGUI.Picture? _previewPic;
    private TGUI.Label?   _noPreviewLabel;
    private TGUI.Label?   _prevMapName, _prevArch, _prevSize, _prevPlayers, _prevRanked, _prevInstalled;

    private List<MapBean> _results = [];
    private int _selectedIndex = -1;
    private const int PageSize = 50;

    // Mod selector — mirrors KnownFeaturedMod display names
    private static readonly string[] ModDisplayNames =
    [
        "Total Annihilation (tacc)",
        "TA: Escalation (taesc)",
        "TA: Zero (tazero)",
        "TA: Mayhem (tamayhem)",
        "TA: VMod Custom (tavmod)",
        "TA: Total War (tatw)",
        "Co-op (coop)",
        "Ladder 1v1 (ladder1v1)",
    ];
    private static readonly string[] ModTechnicalNames =
        ["tacc", "taesc", "tazero", "tamayhem", "tavmod", "tatw", "coop", "ladder1v1"];

    /// <summary>The mod currently selected in the dropdown — drives install checks and downloads.</summary>
    private string SelectedMod =>
        _modCombo != null && _modCombo.SelectedItemIndex >= 0 && _modCombo.SelectedItemIndex < ModTechnicalNames.Length
            ? ModTechnicalNames[_modCombo.SelectedItemIndex]
            : "taesc";

    // Thumbnail prefetch cache: MapName → PNG bytes (null = fetch failed)
    private readonly Dictionary<string, byte[]?> _thumbCache = new();
    // Cancel in-flight prefetch batch when a new search starts
    private CancellationTokenSource _prefetchCts = new();

    public MapSearchWidget(MapService mapService, UiThreadQueue uiQ)
    { _mapService = mapService; _uiQ = uiQ; }

    public void Build(TGUI.Panel parent, float w, float h)
    {
        const float pad = 8f, barH = 32f, split = 0.66f;
        float modBarH  = 30f;
        float listW   = w * split - pad * 1.5f;
        float prevW   = w * (1f - split) - pad * 1.5f;
        float listY   = pad + modBarH + 6f + barH + 6f;
        float listH   = h - listY - barH - pad * 3f;

        // ── Mod selector + scan status bar (NEW) ─────────────────────────────────
        var modLbl = new TGUI.Label();
        modLbl.Text = "Mod:"; modLbl.TextSize = 12;
        modLbl.Position = new TGUI.Vector2f(pad, pad + 6f);
        modLbl.Renderer.SetProperty("TextColor", Theme.Rgb(158, 165, 185));
        parent.Add(modLbl);

        _modCombo = new TGUI.ComboBox(); _modCombo.TextSize = 12;
        _modCombo.Position = new TGUI.Vector2f(pad + 40f, pad);
        _modCombo.Size     = new TGUI.Vector2f(220f, modBarH);
        foreach (var n in ModDisplayNames) _modCombo.AddItem(n);
        _modCombo.SetSelectedItemByIndex(1); // default: taesc
        Theme.ApplyComboBox(_modCombo.Renderer);
        _modCombo.OnItemSelect += (_, _) => OnModChanged();
        parent.Add(_modCombo, "ModCombo");

        _scanBtn = new TGUI.Button(); _scanBtn.Text = "↻ Scan"; _scanBtn.TextSize = 11;
        _scanBtn.Position = new TGUI.Vector2f(pad + 40f + 220f + 8f, pad);
        _scanBtn.Size     = new TGUI.Vector2f(72f, modBarH);
        Theme.ApplySecondaryButton(_scanBtn.Renderer);
        _scanBtn.OnPress += (_, _) => DoScanSelectedMod();
        parent.Add(_scanBtn, "ScanBtn");

        _modStatusLabel = new TGUI.Label();
        _modStatusLabel.TextSize = 11;
        _modStatusLabel.Position = new TGUI.Vector2f(pad + 40f + 220f + 8f + 80f, pad + 7f);
        _modStatusLabel.Size     = new TGUI.Vector2f(listW - (40f + 220f + 8f + 80f), modBarH);
        _modStatusLabel.Renderer.SetProperty("TextColor", Theme.Rgb(120, 130, 155));
        parent.Add(_modStatusLabel, "ModStatusLabel");

        // Subscribe to background scan completion so the status updates live
        // even when triggered from Settings or app startup, not just this widget.
        _mapService.MapsRefreshed += OnMapsRefreshedFromService;

        float topOffset = pad + modBarH + 6f;

        // ── Search bar ─────────────────────────────────────────────────────────
        var searchLbl = new TGUI.Label();
        searchLbl.Text = "Map name:"; searchLbl.TextSize = 12;
        searchLbl.Position = new TGUI.Vector2f(pad, topOffset + 7f);
        searchLbl.Renderer.SetProperty("TextColor", Theme.Rgb(158, 165, 185));
        parent.Add(searchLbl);

        float searchW = listW - 78f - 150f - pad;
        _searchBox = new TGUI.EditBox();
        _searchBox.Position    = new TGUI.Vector2f(pad + 78f, topOffset);
        _searchBox.Size        = new TGUI.Vector2f(searchW, barH);
        _searchBox.DefaultText = "Enter map name…";
        _searchBox.TextSize    = 12;
        Theme.ApplyEditBox(_searchBox.Renderer);
        _searchBox.OnReturnKeyPress += (_, _) => DoSearch();
        parent.Add(_searchBox, "MapSearch");

        var sortLbl = new TGUI.Label();
        sortLbl.Text = "Sort:"; sortLbl.TextSize = 12;
        sortLbl.Position = new TGUI.Vector2f(pad + 78f + searchW + pad, topOffset + 7f);
        sortLbl.Renderer.SetProperty("TextColor", Theme.Rgb(158, 165, 185));
        parent.Add(sortLbl);

        _sortCombo = new TGUI.ComboBox(); _sortCombo.TextSize = 12;
        _sortCombo.Position = new TGUI.Vector2f(pad + 78f + searchW + pad + 40f, topOffset);
        _sortCombo.Size     = new TGUI.Vector2f(100f, barH);
        _sortCombo.AddItem("Newest"); _sortCombo.AddItem("Most played"); _sortCombo.AddItem("A→Z");
        _sortCombo.SetSelectedItemByIndex(0);
        Theme.ApplyComboBox(_sortCombo.Renderer);
        parent.Add(_sortCombo, "SortCombo");

        _searchBtn = new TGUI.Button(); _searchBtn.Text = "Search"; _searchBtn.TextSize = 12;
        _searchBtn.Position = new TGUI.Vector2f(pad + listW - 86f, topOffset);
        _searchBtn.Size     = new TGUI.Vector2f(86f, barH);
        Theme.ApplyPrimaryButton(_searchBtn.Renderer);
        _searchBtn.OnPress += (_, _) => DoSearch();
        parent.Add(_searchBtn, "SearchBtn");

        // ── Map list (left) ────────────────────────────────────────────────────
        _list = new TGUI.ListView();
        _list.Position = new TGUI.Vector2f(pad, listY);
        _list.Size     = new TGUI.Vector2f(listW, listH);
        _list.TextSize = 12; _list.HeaderTextSize = 12;
        Theme.ApplyListView(_list.Renderer);
        _list.ShowHorizontalGridLines = true;
        _list.AddColumn("Map Name",  (uint)(listW * 0.42f));
        _list.AddColumn("Players",   58);
        _list.AddColumn("Size",      65);
        _list.AddColumn("Ranked",    55);
        _list.AddColumn("Installed", 62);
        _list.OnItemSelect += (_, a) => OnSelected(a.Index);
        parent.Add(_list, "MapList");

        // ── Status + bottom bar ────────────────────────────────────────────────
        float by = listY + listH + pad;

        _downloadBtn = new TGUI.Button(); _downloadBtn.Text = "⬇ Download"; _downloadBtn.TextSize = 12;
        _downloadBtn.Position = new TGUI.Vector2f(pad, by);
        _downloadBtn.Size     = new TGUI.Vector2f(120f, barH);
        _downloadBtn.Enabled  = false;
        Theme.ApplyPrimaryButton(_downloadBtn.Renderer);
        _downloadBtn.OnPress += (_, _) => DoDownload();
        parent.Add(_downloadBtn, "DownloadBtn");

        var refreshBtn = new TGUI.Button(); refreshBtn.Text = "↻ Refresh"; refreshBtn.TextSize = 11;
        refreshBtn.Position = new TGUI.Vector2f(pad + 128f, by);
        refreshBtn.Size     = new TGUI.Vector2f(90f, barH);
        Theme.ApplySecondaryButton(refreshBtn.Renderer);
        refreshBtn.OnPress += (_, _) => RefreshInstalled();
        parent.Add(refreshBtn);

        _progressBar = new TGUI.ProgressBar();
        _progressBar.Position = new TGUI.Vector2f(pad + 226f, by);
        _progressBar.Size     = new TGUI.Vector2f(180f, barH);
        _progressBar.Minimum = 0; _progressBar.Maximum = 100; _progressBar.Value = 0;
        _progressBar.Renderer.SetProperty("BackgroundColor", Theme.Rgb(30, 34, 44));
        _progressBar.Renderer.SetProperty("FillColor",       Theme.Rgb(50, 88, 175));
        _progressBar.Renderer.SetProperty("BorderColor",     Theme.Rgb(44, 50, 65));
        _progressBar.Renderer.SetProperty("TextColor",       Theme.Rgb(195, 205, 230));
        _progressBar.Renderer.SetProperty("Borders",         "1");
        _progressBar.Visible = false;
        parent.Add(_progressBar, "DLProgress");

        _statusLabel = new TGUI.Label();
        _statusLabel.Text     = "Enter a map name and press Search";
        _statusLabel.TextSize = 11;
        _statusLabel.Position = new TGUI.Vector2f(pad, by + 8f);
        _statusLabel.Renderer.SetProperty("TextColor", Theme.Rgb(120, 130, 155));
        parent.Add(_statusLabel, "StatusLabel");

        // ── Preview panel (right) ──────────────────────────────────────────────
        float px = pad + listW + pad;
        _previewPanel = new TGUI.Panel();
        _previewPanel.Position = new TGUI.Vector2f(px, listY);
        _previewPanel.Size     = new TGUI.Vector2f(prevW, listH + barH + pad);
        _previewPanel.Renderer.SetProperty("BackgroundColor", Theme.Rgb(24, 27, 36));
        _previewPanel.Renderer.SetProperty("BorderColor",     Theme.Rgb(44, 50, 65));
        _previewPanel.Renderer.SetProperty("Borders",         "1");
        parent.Add(_previewPanel, "PreviewPanel");

        // Placeholder "no preview" label
        _noPreviewLabel = new TGUI.Label();
        _noPreviewLabel.Text     = "Select a map\nto see preview";
        _noPreviewLabel.TextSize = 13;
        _noPreviewLabel.Position = new TGUI.Vector2f(10f, prevW * 0.3f);
        _noPreviewLabel.Size     = new TGUI.Vector2f(prevW - 20f, 50f);
        _noPreviewLabel.HorizontalAlignment = TGUI.HorizontalAlignment.Center;
        _noPreviewLabel.Renderer.SetProperty("TextColor", Theme.Rgb(70, 80, 105));
        _previewPanel.Add(_noPreviewLabel);

        // Preview image slot — will be replaced when a map is selected
        float detailY = prevW + 10f;  // below image
        _prevMapName = DetailLabel(_previewPanel, "—", 10f, detailY, prevW - 20f, 14, bold: true); detailY += 22f;
        _prevArch    = SmallLabel(_previewPanel, "—", 10f, detailY, prevW - 20f); detailY += 18f;
        _prevSize    = SmallLabel(_previewPanel, "—", 10f, detailY, prevW - 20f); detailY += 18f;
        _prevPlayers = SmallLabel(_previewPanel, "—", 10f, detailY, prevW - 20f); detailY += 18f;
        _prevRanked  = SmallLabel(_previewPanel, "—", 10f, detailY, prevW - 20f); detailY += 18f;
        _prevInstalled = SmallLabel(_previewPanel, "—", 10f, detailY, prevW - 20f);

        // Show initial status for the default-selected mod
        UpdateModStatus();
    }

    // ── Mod selector / scan status ───────────────────────────────────────────

    private void OnModChanged()
    {
        // Re-check installed status for the currently visible search results
        // against the newly selected mod, and refresh the status line.
        RefreshInstalled();
        if (_selectedIndex >= 0 && _selectedIndex < _results.Count)
            UpdateDetailLabels(_results[_selectedIndex]);
        UpdateModStatus();
    }

    private void DoScanSelectedMod()
    {
        string mod = SelectedMod;
        if (_scanBtn != null) _scanBtn.Enabled = false;
        if (_modStatusLabel != null) _modStatusLabel.Text = $"Scanning {mod}…";

        Task.Run(async () =>
        {
            await _mapService.RefreshModAsync(mod, force: true);
            _uiQ.Post(() =>
            {
                if (_scanBtn != null) _scanBtn.Enabled = true;
                UpdateModStatus();
                RefreshInstalled();
            });
        });
    }

    /// <summary>Called from MapService on a background thread whenever any mod's scan completes.</summary>
    private void OnMapsRefreshedFromService(string mod)
    {
        if (mod != SelectedMod) return;
        _uiQ.Post(() =>
        {
            UpdateModStatus();
            RefreshInstalled();
        });
    }

    /// <summary>Updates the status label with map count for the selected mod.</summary>
    private void UpdateModStatus()
    {
        if (_modStatusLabel == null) return;
        string mod = SelectedMod;
        var maps = _mapService.GetInstalledMaps(mod);
        _modStatusLabel.Text = maps.Count > 0
            ? $"{maps.Count} map(s) indexed for {mod}"
            : $"No maps indexed for {mod} — click Scan";
    }

    /// <summary>Call when this widget is being torn down, to unsubscribe from MapService events.</summary>
    public void Dispose()
    {
        _mapService.MapsRefreshed -= OnMapsRefreshedFromService;
    }

    // ── Search ─────────────────────────────────────────────────────────────────

    private void DoSearch()
    {
        string q = _searchBox?.Text.Trim() ?? "";
        SetStatus("Searching…");
        if (_searchBtn != null) _searchBtn.Enabled = false;

        // Cancel any in-flight prefetch from previous search
        _prefetchCts.Cancel();
        _prefetchCts = new CancellationTokenSource();
        _thumbCache.Clear();

        var (sortBy, desc) = _sortCombo?.SelectedItemIndex switch
        {
            1 => ("gamesPlayed", true),
            2 => ("displayName", false),
            _ => ("latestVersion.createTime", true),
        };

        Task.Run(async () =>
        {
            try
            {
                var (maps, total) = await _mapService.SearchMapsAsync(
                    nameFilter: q.Length > 0 ? q : null,
                    pageSize: PageSize, sortBy: sortBy, sortDesc: desc);
                _results = maps;
                var prefetchToken = _prefetchCts.Token;
                _uiQ.Post(() =>
                {
                    PopulateList();
                    SetStatus(total > 0 ? $"{total} map(s) found — loading previews…" : "No maps found");
                });
                // Prefetch thumbnails in background with bounded parallelism (4 at a time)
                _ = PrefetchThumbnailsAsync(maps, prefetchToken);
            }
            catch (Exception ex)
            {
                _uiQ.Post(() => SetStatus($"Search failed: {ex.Message}"));
            }
            finally
            {
                _uiQ.Post(() => { if (_searchBtn != null) _searchBtn.Enabled = true; });
            }
        });
    }

    private void PopulateList()
    {
        if (_list == null) return;
        _list.RemoveAllItems();
        foreach (var m in _results)
        {
            bool inst = IsInstalledAnyMod(m.HpiArchiveName);
            _list.AddItem(new[]
            {
                m.DisplayName ?? m.MapName,
                m.MaxPlayers > 0 ? m.MaxPlayers.ToString() : "—",
                m.Width > 0 ? $"{m.Width}×{m.Height} tiles" : "—",
                m.Ranked ? "✓" : "—",
                inst ? "✓" : "—",
            });
        }
        if (_downloadBtn != null) _downloadBtn.Enabled = false;
        ClearPreview();
    }

    // ── Selection → preview ────────────────────────────────────────────────────

    private void OnSelected(int idx)
    {
        _selectedIndex = idx;
        if (_downloadBtn != null)
            _downloadBtn.Enabled = idx >= 0 && idx < _results.Count;

        if (idx < 0 || idx >= _results.Count) { ClearPreview(); return; }
        var map = _results[idx];
        UpdateDetailLabels(map);

        if (string.IsNullOrEmpty(map.MapName)) return;

        // Use cache if already prefetched — shows instantly with no network wait
        if (_thumbCache.TryGetValue(map.MapName, out byte[]? cached))
        {
            ShowPreviewImage(cached, map);
            return;
        }

        // Not cached yet — fetch on demand and store in cache
        Task.Run(async () =>
        {
            byte[]? png = await _mapService.GetThumbnailBytesAsync(map);
            _thumbCache[map.MapName] = png;  // cache for next time
            _uiQ.Post(() =>
            {
                // Only show if this map is still selected
                if (_selectedIndex == idx) ShowPreviewImage(png, map);
            });
        });
    }

    /// <summary>
    /// Fetches thumbnails for all results in parallel (max 4 concurrent),
    /// storing each in _thumbCache as it arrives.
    /// If the selected map's thumbnail lands while it's selected, show it immediately.
    /// </summary>
    private async Task PrefetchThumbnailsAsync(List<MapBean> maps, CancellationToken ct)
    {
        // SemaphoreSlim limits concurrent HTTP requests to avoid hammering the CDN
        using var throttle = new SemaphoreSlim(4);
        int done = 0;
        int total = maps.Count(m => !string.IsNullOrEmpty(m.MapName));

        var tasks = maps
            .Where(m => !string.IsNullOrEmpty(m.MapName))
            .Select(async map =>
            {
                await throttle.WaitAsync(ct);
                try
                {
                    if (ct.IsCancellationRequested) return;
                    byte[]? png = await _mapService.GetThumbnailBytesAsync(map, ct);
                    _thumbCache[map.MapName] = png;

                    done++;
                    _uiQ.Post(() =>
                    {
                        // Update status progress
                        SetStatus($"Loading previews… {done}/{total}");

                        // If this is the currently selected map, show it right away
                        if (_selectedIndex >= 0 && _selectedIndex < _results.Count &&
                            _results[_selectedIndex].MapName == map.MapName)
                        {
                            ShowPreviewImage(png, map);
                        }
                    });
                }
                catch (OperationCanceledException) { }
                finally { throttle.Release(); }
            });

        await Task.WhenAll(tasks);

        if (!ct.IsCancellationRequested)
            _uiQ.Post(() => SetStatus($"{maps.Count} map(s) found — previews ready"));
    }

    private void UpdateDetailLabels(MapBean m)
    {
        if (_prevMapName  != null) _prevMapName.Text  = m.DisplayName ?? m.MapName;
        if (_prevArch     != null) _prevArch.Text     = m.HpiArchiveName;
        if (_prevSize     != null) _prevSize.Text     = m.Width > 0 ? $"Size: {m.Width}×{m.Height} tiles" : "Size: —";
        if (_prevPlayers  != null) _prevPlayers.Text  = $"Max players: {(m.MaxPlayers > 0 ? m.MaxPlayers.ToString() : "—")}";
        if (_prevRanked   != null) _prevRanked.Text   = m.Ranked ? "Ranked: ✓ Yes" : "Ranked: —";
        if (_prevInstalled!= null) _prevInstalled.Text= IsInstalledAnyMod(m.HpiArchiveName)
                                                         ? "Installed: ✓ Yes" : "Installed: —";
    }

    private void ShowPreviewImage(byte[]? png, MapBean map)
    {
        if (_previewPanel == null) return;

        if (_previewPic != null)
        {
            _previewPanel.Remove(_previewPic);
            _previewPic.Dispose();
            _previewPic = null;
        }

        if (_noPreviewLabel != null) _noPreviewLabel.Visible = png == null;
        if (png == null) return;

        try
        {
            // TGUI.Texture only accepts a file path in TGUI.Net 1.x.
            // Write the PNG bytes to a temp file, load from there, then delete.
            string tmp = Path.Combine(Path.GetTempPath(), $"taf_preview_{map.MapName}.png");
            File.WriteAllBytes(tmp, png);

            var tguiTex = new TGUI.Texture(tmp);

            // Clean up temp file after texture is loaded
            try { File.Delete(tmp); } catch { }

            // SFML.Graphics.Texture to get image dimensions for aspect ratio
            var sfTex = new SFML.Graphics.Texture(new SFML.Graphics.Image(png));
            float maxW   = (_previewPanel.Size.X - 20f) / 2f;
            float maxH   = (_previewPanel.Size.Y - 20f) / 2f;
            float texW   = sfTex.Size.X > 0 ? sfTex.Size.X : 1f;
            float texH   = sfTex.Size.Y > 0 ? sfTex.Size.Y : 1f;
            float aspect = texH / texW;
            float imgW   = maxW;
            float imgH   = imgW * aspect;
            if (imgH > maxH) { imgH = maxH; imgW = imgH / aspect; }

            // Correct TGUI.Net 1.x API: new Picture(), set Renderer.Texture
            _previewPic = new TGUI.Picture();
            _previewPic.Renderer.SetProperty("Texture", tmp); // set via renderer property
            _previewPic.Position = new TGUI.Vector2f(10f, 8f);
            _previewPic.Size     = new TGUI.Vector2f(imgW, imgH);
            _previewPanel.Add(_previewPic);

            float dy = imgH + 14f;
            ReposLabel(_prevMapName,   dy); dy += 22f;
            ReposLabel(_prevArch,      dy); dy += 18f;
            ReposLabel(_prevSize,      dy); dy += 18f;
            ReposLabel(_prevPlayers,   dy); dy += 18f;
            ReposLabel(_prevRanked,    dy); dy += 18f;
            ReposLabel(_prevInstalled, dy);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PREVIEW] Failed to load image: {ex.Message}");
        }
    }

    private static void ReposLabel(TGUI.Label? lbl, float y)
    {
        if (lbl == null) return;
        lbl.Position = new TGUI.Vector2f(lbl.Position.X, y);
    }

    private void ClearPreview()
    {
        if (_previewPic != null)
        {
            _previewPanel?.Remove(_previewPic);
            _previewPic.Dispose();
            _previewPic = null;
        }
        if (_noPreviewLabel != null) _noPreviewLabel.Visible = true;
        if (_prevMapName   != null) _prevMapName.Text   = "—";
        if (_prevArch      != null) _prevArch.Text      = "—";
        if (_prevSize      != null) _prevSize.Text      = "—";
        if (_prevPlayers   != null) _prevPlayers.Text   = "—";
        if (_prevRanked    != null) _prevRanked.Text    = "—";
        if (_prevInstalled != null) _prevInstalled.Text = "—";
    }

    // ── Download ───────────────────────────────────────────────────────────────

    private void DoDownload()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _results.Count) return;
        var map = _results[_selectedIndex];
        if (string.IsNullOrEmpty(map.HpiArchiveName)) return;

        if (IsInstalledAnyMod(map.HpiArchiveName))
        { SetStatus($"Already installed: {map.HpiArchiveName}"); return; }

        SetStatus($"Downloading {map.HpiArchiveName}…");
        if (_downloadBtn  != null) _downloadBtn.Enabled   = false;
        if (_progressBar  != null) { _progressBar.Value   = 0; _progressBar.Visible = true; }
        if (_statusLabel  != null) _statusLabel.Visible   = false;

        var progress = new Progress<double>(p =>
            _uiQ.Post(() => { if (_progressBar != null) _progressBar.Value = (int)(p * 100); }));

        Task.Run(async () =>
        {
            bool ok = await _mapService.DownloadAndInstallAsync(BestTargetMod(), map.HpiArchiveName, progress);
            _uiQ.Post(() =>
            {
                if (_progressBar  != null) _progressBar.Visible  = false;
                if (_statusLabel  != null) _statusLabel.Visible  = true;
                SetStatus(ok ? $"✓ {map.HpiArchiveName} installed." : $"✗ Download failed.");
                RefreshInstalled();
                UpdateModStatus();
                if (_downloadBtn != null) _downloadBtn.Enabled = true;
                if (_selectedIndex >= 0 && _selectedIndex < _results.Count)
                    UpdateDetailLabels(_results[_selectedIndex]);
            });
        });
    }

    private void RefreshInstalled()
    {
        if (_list == null || _results.Count == 0) return;
        for (int i = 0; i < _results.Count && i < (int)_list.ItemCount; i++)
        {
            bool inst = IsInstalledAnyMod(_results[i].HpiArchiveName);
            _list.ChangeItem(i, new[]
            {
                _list.GetItemCell(i, 0), _list.GetItemCell(i, 1),
                _list.GetItemCell(i, 2), _list.GetItemCell(i, 3),
                inst ? "✓" : "—",
            });
        }
    }

    private void SetStatus(string msg)
    {
        if (_statusLabel != null) _statusLabel.Text = msg;
    }

    // ── Install check across mods ────────────────────────────────────────────

    private static readonly string[] KnownMods =
        ["tacc", "taesc", "tazero", "tamayhem", "tavmod", "tatw", "coop", "ladder1v1"];

    /// <summary>
    /// Returns true if an archive is installed for the currently selected mod.
    /// Falls back to checking all known mods if not found under the selection,
    /// since map archives are sometimes shared/compatible across mods.
    /// </summary>
    private bool IsInstalledAnyMod(string hpiArchiveName)
    {
        if (string.IsNullOrEmpty(hpiArchiveName)) return false;
        if (_mapService.IsInstalled(SelectedMod, hpiArchiveName)) return true;
        return KnownMods.Any(mod => mod != SelectedMod && _mapService.IsInstalled(mod, hpiArchiveName));
    }

    /// <summary>Download target is always the mod selected in the dropdown.</summary>
    private string BestTargetMod() => SelectedMod;

    // ── Label helpers ──────────────────────────────────────────────────────────

    private static TGUI.Label DetailLabel(TGUI.Container p, string text,
        float x, float y, float w, uint size, bool bold)
    {
        var l = new TGUI.Label(); l.Text = text; l.TextSize = (int)size;
        l.Position = new TGUI.Vector2f(x, y); l.Size = new TGUI.Vector2f(w, 20f);
        l.Renderer.SetProperty("TextColor", bold ? Theme.Rgb(205, 215, 255) : Theme.Rgb(180, 188, 210));
        if (bold) l.Renderer.SetProperty("TextStyle", "Bold");
        p.Add(l); return l;
    }

    private static TGUI.Label SmallLabel(TGUI.Container p, string text, float x, float y, float w)
    {
        var l = new TGUI.Label(); l.Text = text; l.TextSize = 12;
        l.Position = new TGUI.Vector2f(x, y); l.Size = new TGUI.Vector2f(w, 18f);
        l.Renderer.SetProperty("TextColor", Theme.Rgb(155, 165, 188));
        p.Add(l); return l;
    }
}
