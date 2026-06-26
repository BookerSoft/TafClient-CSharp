using TafClient.Domain;
using TafClient.Net.Domain;
using TafClient.Service;
using TafClient.UI;

namespace TafClient.UI.Widgets;

/// <summary>
/// Port of CreateGameController.java.
///
/// Layout (mirrors original):
///   Left panel:
///     - Featured mod ListView  (featuredModListView)
///     - Map search EditBox     (mapSearchTextField)
///     - Map ListView           (mapListView)
///     - Random map button
///   Right panel:
///     - Map preview image      (mapPreviewPane)
///     - Map size / players / description labels
///     - Game title EditBox     (titleTextField)
///     - Password EditBox       (passwordTextField)
///     - Max players ComboBox   (maxPlayersComboBox)
///     - Ranked checkbox        (rankedEnabledCheckBox)
///     - Rating range fields    (minRankingTextField / maxRankingTextField / enforceRankingCheckBox)
///     - Friends only checkbox  (onlyForFriendsCheckBox)
///     - Create / Cancel buttons
/// </summary>
public sealed class HostGameDialog
{
    private readonly TGUI.Gui      _gui;
    private readonly GameService   _gs;
    private readonly MapService    _ms;
    private readonly PreferencesService _prefs;
    private readonly UserService   _us;
    private readonly GameLaunchService _gls;
    private readonly UiThreadQueue _uiQ;

    // Left panel widgets
    private TGUI.ListView? _modList;
    private TGUI.ListView? _mapList;
    private TGUI.EditBox?  _mapSearch;

    // Right panel widgets
    private TGUI.Picture?  _mapPreview;
    private TGUI.Label?    _noPreviewLbl;
    private TGUI.Panel?    _previewPanel;   // stored to avoid GetChild lookup
    private TGUI.Label?    _mapSizeLbl, _mapPlayersLbl, _mapDescLbl, _mapArchiveLbl;
    private TGUI.EditBox?  _title, _password, _minRating, _maxRating;
    private TGUI.ComboBox? _maxPlayers;
    private TGUI.CheckBox? _ranked, _enforce, _friendsOnly;
    private TGUI.ListView? _rankedPoolList;
    private TGUI.Panel?    _enforceContainer;
    private TGUI.Button?   _createBtn, _cancelBtn;

    // Data
    private List<MapBean> _allMaps = [];
    private List<MapBean> _filteredMaps = [];
    private TGUI.ChildWindow? _win;

    // Mod arrays
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
    [
        "tacc", "taesc", "tazero", "tamayhem", "tavmod", "tatw", "coop", "ladder1v1",
    ];

    // Known matchmaking-queue/leaderboard technical names — mirrors the
    // RatingType value sent to the server (LeaderboardRating is keyed by
    // these same names: "global", "ladder1v1", etc.). The real client
    // populates mapPoolListView from a live server query per mod; we list
    // the known queues directly since we don't have that API wired up.
    private static readonly string[] RankedPoolDisplayNames = ["Global", "1v1 Ladder"];
    private static readonly string[] RankedPoolTechnicalNames = ["global", "ladder1v1"];

    public HostGameDialog(TGUI.Gui gui, GameService gs, MapService ms, UiThreadQueue uiQ, PreferencesService prefs, UserService us, GameLaunchService gls)
    { _gui = gui; _gs = gs; _ms = ms; _uiQ = uiQ; _prefs = prefs; _us = us; _gls = gls; }

    public void Show()
    {
        const float dw = 860f, dh = 680f;
        _win = new TGUI.ChildWindow();
        _win.Title     = "Host a Game";
        _win.Size      = new TGUI.Vector2f(dw, dh);
        _win.Position  = new TGUI.Vector2f(
            (_gui.GetView().Size.X - dw) / 2f,
            (_gui.GetView().Size.Y - dh) / 2f);
        _win.Resizable = false;
        Theme.ApplyChildWindow(_win.Renderer);
        // NOTE: deliberately NOT calling _win.Dispose() here, unlike the
        // other close paths in this file (_cancelBtn.OnPress, the end of
        // DoHost). This callback runs from WITHIN TGUI's own native
        // close-signal processing (ChildWindow::ProcessClosedSignal) — a
        // known TGUI.Net issue (texus/TGUI.Net#3) shows that triggering
        // further widget/parent access from inside that exact call stack
        // can throw MissingMethodException. Remove() alone is safe here;
        // the underlying native object will still be released once the
        // C# wrapper is eventually GC'd, just not as promptly as the
        // other paths that dispose explicitly.
        _win.OnClose += (_, _) => _gui.Remove(_win);

        const float pad = 12f;
        float leftW  = 270f;
        float rightX = pad + leftW + pad;
        float rightW = dw - rightX - pad;
        float h      = dh - pad * 2f;

        BuildLeftPanel(pad, pad, leftW, h);
        BuildRightPanel(rightX, pad, rightW, h);

        _gui.Add(_win, "HostDialog");

        // Initial mod selection → load maps
        SelectMod(0);
        UpdateRankedState();
    }

    // ── Left panel ────────────────────────────────────────────────────────────

    private void BuildLeftPanel(float x, float y, float w, float h)
    {
        // Section label
        SectionLbl(_win!, "Mod:", x, y);  y += 18f;

        // Featured mod ListView — mirrors featuredModListView
        _modList = new TGUI.ListView();
        _modList.Position = new TGUI.Vector2f(x, y);
        _modList.Size     = new TGUI.Vector2f(w, 160f);
        _modList.TextSize = 12; _modList.HeaderTextSize = 0;
        Theme.ApplyListView(_modList.Renderer);
        foreach (var n in ModDisplayNames) _modList.AddItem([n]);
        _modList.OnItemSelect += (_, a) => { if (a.Index >= 0) SelectMod(a.Index); };
        _win!.Add(_modList);
        y += 168f;

        // Open Mod Folder — mirrors openGameFolderButton
        var openFolderBtn = new TGUI.Button(); openFolderBtn.Text = "Open Mod Folder"; openFolderBtn.TextSize = 11;
        openFolderBtn.Position = new TGUI.Vector2f(x, y);
        openFolderBtn.Size     = new TGUI.Vector2f(w, 26f);
        Theme.ApplySecondaryButton(openFolderBtn.Renderer);
        openFolderBtn.OnPress += (_, _) => OpenModFolder();
        _win.Add(openFolderBtn);
        y += 30f;

        // Install Mod — mirrors installGameButton
        var installBtn = new TGUI.Button(); installBtn.Text = "Install Mod"; installBtn.TextSize = 11;
        installBtn.Position = new TGUI.Vector2f(x, y);
        installBtn.Size     = new TGUI.Vector2f(w, 26f);
        Theme.ApplySecondaryButton(installBtn.Renderer);
        installBtn.OnPress += (_, _) => InstallSelectedMod();
        _win.Add(installBtn);
        y += 30f;

        // Map search — mirrors mapSearchTextField
        SectionLbl(_win, "Map Search:", x, y); y += 18f;
        _mapSearch = new TGUI.EditBox();
        _mapSearch.Position    = new TGUI.Vector2f(x, y);
        _mapSearch.Size        = new TGUI.Vector2f(w - 36f, 26f);
        _mapSearch.DefaultText = "Filter maps…";
        _mapSearch.TextSize    = 12;
        Theme.ApplyEditBox(_mapSearch.Renderer);
        _mapSearch.OnTextChange += (_, _) => FilterMaps();
        _win.Add(_mapSearch);

        // Random map button — mirrors randomMapButton
        var rnd = new TGUI.Button(); rnd.Text = "🎲"; rnd.TextSize = 14;
        rnd.Position = new TGUI.Vector2f(x + w - 34f, y);
        rnd.Size     = new TGUI.Vector2f(34f, 26f);
        Theme.ApplySecondaryButton(rnd.Renderer);
        rnd.OnPress += (_, _) => RandomMap();
        _win.Add(rnd);
        y += 32f;

        // Map ListView — mirrors mapListView
        float listH = h - y + 12f;
        _mapList = new TGUI.ListView();
        _mapList.Position = new TGUI.Vector2f(x, y);
        _mapList.Size     = new TGUI.Vector2f(w, listH);
        _mapList.TextSize = 12; _mapList.HeaderTextSize = 0;
        Theme.ApplyListView(_mapList.Renderer);
        _mapList.OnItemSelect += (_, a) => { if (a.Index >= 0) SelectMap(a.Index); };
        _win.Add(_mapList);
    }

    // ── Right panel ───────────────────────────────────────────────────────────

    private void BuildRightPanel(float x, float y, float w, float h)
    {
        const float rh = 26f, gap = 6f, lw = 100f;

        // Map preview pane — mirrors mapPreviewPane
        float prevH = 160f;
        _previewPanel = new TGUI.Panel();
        _previewPanel.Position = new TGUI.Vector2f(x, y);
        _previewPanel.Size     = new TGUI.Vector2f(w, prevH);
        _previewPanel.Renderer.SetProperty("BackgroundColor", Theme.Rgb(20, 22, 28));
        _previewPanel.Renderer.SetProperty("BorderColor",     Theme.Rgb(44, 50, 65));
        _previewPanel.Renderer.SetProperty("Borders",         "1");
        _win!.Add(_previewPanel, "PreviewPanel");

        _noPreviewLbl = new TGUI.Label();
        _noPreviewLbl.Text      = "No map selected";
        _noPreviewLbl.TextSize  = 12;
        _noPreviewLbl.Position  = new TGUI.Vector2f(4f, prevH / 2f - 10f);
        _noPreviewLbl.Size      = new TGUI.Vector2f(w - 8f, 20f);
        _noPreviewLbl.HorizontalAlignment = TGUI.HorizontalAlignment.Center;
        _noPreviewLbl.Renderer.SetProperty("TextColor", Theme.Rgb(80, 90, 110));
        _previewPanel.Add(_noPreviewLbl);

        y += prevH + gap;

        // Map info labels — mirrors mapSizeLabel, mapPlayersLabel, mapDescriptionLabel
        _mapSizeLbl    = InfoLbl(_win, x, y, w); y += 18f;
        _mapPlayersLbl = InfoLbl(_win, x, y, w); y += 18f;
        _mapArchiveLbl = InfoLbl(_win, x, y, w); y += 18f;
        _mapDescLbl    = InfoLbl(_win, x, y, w); y += 22f;

        // Title — mirrors titleTextField
        Row(_win, "Title:", x, ref y, lw, w - lw - 4f, rh, gap, out _title, "e.g. 3v3 No Rush 30");

        // Password — mirrors passwordTextField
        Row(_win, "Password:", x, ref y, lw, w - lw - 4f, rh, gap, out _password, "blank = public");
        _password!.PasswordCharacter = "*";

        // Max players — mirrors maxPlayersComboBox (2..10)
        Lbl(_win, "Max Players:", x, y + 5f, lw);
        _maxPlayers = new TGUI.ComboBox(); _maxPlayers.TextSize = 12;
        _maxPlayers.Position = new TGUI.Vector2f(x + lw + 4f, y);
        _maxPlayers.Size     = new TGUI.Vector2f(70f, rh);
        for (int i = 2; i <= 10; i++) _maxPlayers.AddItem(i.ToString());
        _maxPlayers.SetSelectedItemByIndex(8); // default 10
        Theme.ApplyComboBox(_maxPlayers.Renderer);
        _win.Add(_maxPlayers);
        y += rh + gap;

        // Ranked — mirrors rankedEnabledCheckBox
        _ranked = new TGUI.CheckBox();
        _ranked.Text = "Ranked game"; _ranked.TextSize = 12; _ranked.Checked = true;
        _ranked.Position = new TGUI.Vector2f(x, y);
        _ranked.Size     = new TGUI.Vector2f(20f, 20f);
        Theme.ApplyCheckBox(_ranked.Renderer);
        _ranked.OnCheck   += (_, _) => UpdateRankedState();
        _ranked.OnUncheck += (_, _) => UpdateRankedState();
        _win.Add(_ranked);
        y += 28f + gap;

        // Ranked pool list — mirrors mapPoolListView. Only visible/usable when
        // "Ranked game" is checked. Selecting a pool determines which
        // leaderboard/matchmaking queue this game's rating counts toward
        // (sent to the server as RatingType — "global", "ladder1v1", etc.).
        // NOTE: the real client populates this from a live server query for
        // the mod's available matchmaking queues; we don't have that API
        // wired up, so this lists the known queue technical names directly.
        // "Global" is selected by default, matching the real client's default
        // and the only pool every mod is guaranteed to have.
        SectionLbl(_win, "Ranked Pool:", x, y); y += 18f;
        _rankedPoolList = new TGUI.ListView();
        _rankedPoolList.Position = new TGUI.Vector2f(x, y);
        _rankedPoolList.Size     = new TGUI.Vector2f(w, 70f);
        _rankedPoolList.TextSize = 12; _rankedPoolList.HeaderTextSize = 0;
        Theme.ApplyListView(_rankedPoolList.Renderer);
        foreach (var n in RankedPoolDisplayNames) _rankedPoolList.AddItem([n]);
        // NOTE: TGUI.Net's ListView (unlike ComboBox) has no SetSelectedItemByIndex
        // in this version, and SelectedItemIndex is read-only — so we can't
        // pre-select "Global" visually here. DoHost()'s poolIdx lookup already
        // defaults to 0 ("global") via SelectedItemIndex ?? 0 when nothing has
        // been explicitly clicked, so behavior is correct even without a
        // visible default selection; the user can still click "Global" to
        // highlight it themselves.
        _win.Add(_rankedPoolList, "RankedPoolList");
        y += 78f + gap;

        // Rating range container — mirrors enforceRankingContainer + rankingRangeContainer
        _enforceContainer = new TGUI.Panel();
        _enforceContainer.Position = new TGUI.Vector2f(x, y);
        _enforceContainer.Size     = new TGUI.Vector2f(w, 60f);
        _enforceContainer.Renderer.SetProperty("BackgroundColor", "rgba(0,0,0,0)");
        _enforceContainer.Renderer.SetProperty("Borders", "0");
        {
            float ey = 2f;
            Lbl(_enforceContainer, "Min rating:", 0f, ey + 4f, 75f);
            _minRating = EB("none"); Place(_minRating, 78f, ey, 58f, rh);
            _enforceContainer.Add(_minRating);
            Lbl(_enforceContainer, "Max:", 142f, ey + 4f, 36f);
            _maxRating = EB("none"); Place(_maxRating, 180f, ey, 58f, rh);
            _enforceContainer.Add(_maxRating);
            ey += rh + 4f;
            _enforce = new TGUI.CheckBox();
            _enforce.Text = "Enforce rating range"; _enforce.TextSize = 12;
            _enforce.Position = new TGUI.Vector2f(0f, ey);
            _enforce.Size     = new TGUI.Vector2f(20f, 20f);
            Theme.ApplyCheckBox(_enforce.Renderer);
            _enforceContainer.Add(_enforce);
        }
        _win.Add(_enforceContainer);
        y += 68f;

        // Friends only — mirrors onlyForFriendsCheckBox
        _friendsOnly = new TGUI.CheckBox();
        _friendsOnly.Text = "Friends only"; _friendsOnly.TextSize = 12;
        _friendsOnly.Position = new TGUI.Vector2f(x, y);
        _friendsOnly.Size     = new TGUI.Vector2f(20f, 20f);
        Theme.ApplyCheckBox(_friendsOnly.Renderer);
        _win.Add(_friendsOnly);
        y += 32f;

        // Buttons
        float bw = (w - 8f) / 2f;
        _createBtn = new TGUI.Button(); _createBtn.Text = "Host Game"; _createBtn.TextSize = 13;
        Place(_createBtn, x, y, bw, 32f);
        Theme.ApplyPrimaryButton(_createBtn.Renderer);
        _createBtn.OnPress += (_, _) => DoHost();
        _win.Add(_createBtn);

        _cancelBtn = new TGUI.Button(); _cancelBtn.Text = "Cancel"; _cancelBtn.TextSize = 13;
        Place(_cancelBtn, x + bw + 8f, y, bw, 32f);
        Theme.ApplySecondaryButton(_cancelBtn.Renderer);
        _cancelBtn.OnPress += (_, _) => { _gui.Remove(_win!); _win!.Dispose(); };
        _win.Add(_cancelBtn);
    }

    // ── Mod selection ─────────────────────────────────────────────────────────

    private void SelectMod(int idx)
    {
        if (idx < 0 || idx >= ModTechnicalNames.Length) return;
        string tech = ModTechnicalNames[idx];
        Console.WriteLine($"[HOST] SelectMod idx={idx} tech={tech}");
        if (OperatingSystem.IsWindows() && !DPlayRegFileGenerator.IsRegistered(tech))
        {
            Console.WriteLine($"[HOST] NOTE: {tech} is not yet DirectPlay-registered. " +
                "If hosting fails or the game window disappears unexpectedly, go to " +
                "Settings and click \"Gen .reg\" for this mod, then double-click the " +
                "generated file and approve the UAC prompt.");
        }
        LoadMaps(tech);
    }

    private void LoadMaps(string modTechnical)
    {
        _allMaps = _ms.GetInstalledMaps(modTechnical).ToList();
        Console.WriteLine($"[HOST] LoadMaps mod={modTechnical} cached={_allMaps.Count}");

        if (_allMaps.Count == 0)
        {
            // Cache empty — show placeholder and wait for background scan to complete
            if (_mapList != null) { _mapList.RemoveAllItems(); _mapList.AddItem(["Scanning…"]); }
            // Subscribe to scan completion so we can refresh when ready
            void OnRefresh(string mod)
            {
                if (mod != modTechnical) return;
                _ms.MapsRefreshed -= OnRefresh;
                _allMaps = _ms.GetInstalledMaps(modTechnical).ToList();
                _uiQ.Post(FilterMaps);
            }
            _ms.MapsRefreshed += OnRefresh;
        }
        else
        {
            FilterMaps();
        }
    }

    private void FilterMaps()
    {
        string filter = _mapSearch?.Text.Trim() ?? "";
        _filteredMaps = string.IsNullOrEmpty(filter)
            ? _allMaps
            : _allMaps.Where(m => m.MapName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        if (_mapList == null) return;
        _mapList.RemoveAllItems();
        foreach (var m in _filteredMaps)
            _mapList.AddItem([m.MapName]);

        if (_filteredMaps.Count > 0)
            SelectMap(0);
    }

    private void RandomMap()
    {
        if (_filteredMaps.Count == 0) return;
        int idx = Random.Shared.Next(_filteredMaps.Count);
        SelectMap(idx);
    }

    /// <summary>Mirrors onOpenGameFolderClicked — reveals the selected mod's install folder.</summary>
    private void OpenModFolder()
    {
        int modIdx = _modList?.SelectedItemIndex ?? -1;
        if (modIdx < 0 || modIdx >= ModTechnicalNames.Length)
        {
            Console.WriteLine("[HOST] OpenModFolder: no mod selected");
            return;
        }
        string tech = ModTechnicalNames[modIdx];
        string? path = _ms.GetMapsFolder(tech);
        if (path is null || !Directory.Exists(path))
        {
            Console.WriteLine($"[HOST] OpenModFolder: path not found for {tech} ({path})");
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start("explorer.exe", path);
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", path);
            else
                System.Diagnostics.Process.Start("xdg-open", path);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HOST] OpenModFolder failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Mirrors onInstallSelectedModClicked — lets the user pick the install
    /// directory for the selected mod, persisted via PreferencesService so
    /// future scans/launches use it (same mechanism as the Settings tab).
    /// </summary>
    private void InstallSelectedMod()
    {
        int modIdx = _modList?.SelectedItemIndex ?? -1;
        if (modIdx < 0 || modIdx >= ModTechnicalNames.Length)
        {
            Console.WriteLine("[HOST] InstallSelectedMod: no mod selected");
            return;
        }
        string tech = ModTechnicalNames[modIdx];
        string? current = _ms.GetMapsFolder(tech);

        ExePathDialog.Show(_gui, $"Select install folder for {tech}", current, _uiQ, chosenPath =>
        {
            _prefs.SetModExePath(tech, chosenPath);
            Console.WriteLine($"[HOST] InstallSelectedMod: set {tech} path to {chosenPath}");
            _ms.RefreshMod(tech, force: true);
            LoadMaps(tech); // refresh the map list against the newly-set path
        });
    }

    private void SelectMap(int idx)
    {
        if (idx < 0 || idx >= _filteredMaps.Count) return;
        var map = _filteredMaps[idx];

        if (_mapSizeLbl    != null) _mapSizeLbl.Text    = map.Width > 0 ? $"Size: {map.Width}×{map.Height} tiles" : "Size: —";
        if (_mapPlayersLbl != null) _mapPlayersLbl.Text = $"Max players: {(map.MaxPlayers > 0 ? map.MaxPlayers.ToString() : "—")}";
        if (_mapArchiveLbl != null) _mapArchiveLbl.Text = $"Archive: {map.HpiArchiveName}";
        if (_mapDescLbl    != null) _mapDescLbl.Text    = map.Description ?? "";

        // Load preview thumbnail in background
        if (!string.IsNullOrEmpty(map.MapName))
            _ = LoadPreviewAsync(map);
    }

    private async Task LoadPreviewAsync(MapBean map)
    {
        byte[]? png = await _ms.GetThumbnailBytesAsync(map);
        _uiQ.Post(() => ShowPreview(png, map));
    }

    private void ShowPreview(byte[]? png, MapBean map)
    {
        if (_previewPanel == null) return;
        if (_mapPreview != null) { _previewPanel.Remove(_mapPreview); _mapPreview.Dispose(); _mapPreview = null; }
        if (_noPreviewLbl != null) _noPreviewLbl.Visible = png == null;
        if (png == null) return;

        try
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"taf_host_prev_{map.MapName}.png");
            File.WriteAllBytes(tmp, png);
            var pic = new TGUI.Picture();
            pic.Renderer.SetProperty("Texture", tmp);
            pic.Position = new TGUI.Vector2f(4f, 4f);
            float pw = _previewPanel.Size.X - 8f, ph = _previewPanel.Size.Y - 8f;
            try
            {
                var img = new SFML.Graphics.Image(png);
                float aspect = img.Size.X > 0 ? (float)img.Size.Y / img.Size.X : 1f;
                float iw = pw / 2f, ih = iw * aspect;
                if (ih > ph / 2f) { ih = ph / 2f; iw = ih / aspect; }
                pic.Size = new TGUI.Vector2f(iw, ih);
            }
            catch { pic.Size = new TGUI.Vector2f(pw / 2f, ph / 2f); }
            try { File.Delete(tmp); } catch { }
            _mapPreview = pic;
            _previewPanel.Add(pic);
        }
        catch (Exception ex) { Console.WriteLine($"[HOST] Preview error: {ex.Message}"); }
    }

    private void UpdateRankedState()
    {
        bool r = _ranked?.Checked ?? true;
        if (_enforceContainer != null) _enforceContainer.Enabled = r;
        if (_rankedPoolList != null) _rankedPoolList.Visible = r;
    }

    // ── Host ──────────────────────────────────────────────────────────────────

    private void DoHost()
    {
        int modIdx = _modList?.SelectedItemIndex ?? 0;
        string modTech = modIdx >= 0 && modIdx < ModTechnicalNames.Length
            ? ModTechnicalNames[modIdx] : "tacc";

        string mapName = "";
        int mapIdx = _mapList?.SelectedItemIndex ?? -1;
        if (mapIdx >= 0 && mapIdx < _filteredMaps.Count)
            mapName = _filteredMaps[mapIdx].MapName;
        if (string.IsNullOrEmpty(mapName)) { Console.WriteLine("[HOST] No map selected"); return; }

        bool ranked        = _ranked?.Checked ?? true;
        int  poolIdx       = _rankedPoolList?.SelectedItemIndex ?? 0;
        string ratingType  = (poolIdx >= 0 && poolIdx < RankedPoolTechnicalNames.Length)
            ? RankedPoolTechnicalNames[poolIdx] : "global";
        bool enforceRating = ranked && (_enforce?.Checked ?? false);

        int? minR = null, maxR = null;
        if (ranked)
        {
            if (int.TryParse(_minRating?.Text, out int mn)) minR = mn;
            if (int.TryParse(_maxRating?.Text, out int mx)) maxR = mx;
        }

        int maxPlayers = int.TryParse(_maxPlayers?.SelectedItem, out int mp) ? mp : 10;

        // Write TAForever.ini immediately, before sending the host request at
        // all — TA's own engine reads this file at startup to learn the
        // session name, mission/map, player limit, and lock-options setting.
        // Previously this only happened deep inside gpgnet4ta's own HostGame
        // event handler, which is the very last step of a long chain
        // (talauncher → ICE adapter → RPC connect → DirectPlay → gpgnet4ta).
        // Writing it here, synchronously, the moment the button is pressed,
        // means the file is guaranteed to exist with the dialog's exact
        // settings well before any of that machinery even starts — gpgnet4ta
        // still writes it again later as a safety net using whatever the
        // server's game_launch response reports, in case this write didn't
        // happen for some reason (e.g. an older client driving the same
        // gpgnet4ta build).
        string? gamePath = _ms.GetMapsFolder(modTech);
        if (gamePath is not null)
        {
            // NOTE: "lock options" is TA's own session setting (locks game
            // options from being changed once hosted) — a different concept
            // from "enforce rating range" above. There's no UI control for it
            // in this dialog yet, so default to false (unlocked) rather than
            // incorrectly reusing enforceRating here.
            bool wrote = TaInitFileWriter.Write(
                gamePath, _us.Username ?? "Player", mapName, maxPlayers, lockOptions: false, out string? iniError);
            if (!wrote)
                Console.WriteLine($"[HOST] Could not pre-write TAForever.ini: {iniError ?? "(unknown reason)"} — gpgnet4ta will still try its own write later");
        }
        else
        {
            Console.WriteLine($"[HOST] No game path resolved for mod '{modTech}' — skipping early TAForever.ini write");
        }

        Console.WriteLine($"[HOST] Sending host request: map='{mapName}' mod='{modTech}' title='{_title?.Text?.Trim()}'");

        var hostTask = _gs.HostGameAsync(new NewGameInfo
        {
            Title                    = _title?.Text?.Trim() ?? "Unnamed",
            Password                 = string.IsNullOrEmpty(_password?.Text) ? null : _password.Text,
            FeaturedModTechnicalName = modTech,
            Map                      = mapName,
            Visibility               = _friendsOnly?.Checked == true ? GameVisibility.Private : GameVisibility.Public,
            RatingType               = ranked ? ratingType : "global",
            RatingMin                = minR,
            RatingMax                = maxR,
            EnforceRatingRange       = enforceRating ? true : null,
            MaxPlayers               = maxPlayers,
        });

        // Show the staging/lobby room screen using game_launch's own
        // result.Uid directly, the moment it resolves — NOT by waiting on
        // CurrentGameChanged. CurrentGame only updates when the player's
        // OWN CurrentGameUid field changes via a separate player_info
        // server push (see GameService.OnGameInfo / Player.CurrentGameUid)
        // — that's a different, less direct signal than the uid we
        // already have immediately from game_launch's response, and it
        // may be delayed or not reliably tied to this exact timing at all.
        // GetByUid looks up the Game object itself (populated from
        // game_info, which the server broadcasts to everyone for any open
        // game — not specifically gated on "is this player in it") — this
        // is confirmed as the more direct, reliable path.
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await hostTask.WaitAsync(TimeSpan.FromSeconds(30));
                if (result is null)
                {
                    Console.WriteLine($"[HOST] Server rejected the host request — reason: {_gs.LastGameLaunchFailReason ?? "(no reason given)"}");
                    return;
                }
                Console.WriteLine($"[HOST] game_launch received: uid={result.Uid} map={result.Mapname} mod={result.Mod}");

                // The Game object (from game_info) may not have arrived at
                // this exact instant even though game_launch already has —
                // these are two separate server messages. Retry briefly
                // rather than giving up after one failed lookup.
                Game? game = null;
                for (int attempt = 0; attempt < 20 && game is null; attempt++)
                {
                    game = _gs.GetByUid(result.Uid);
                    if (game is null) await Task.Delay(250);
                }

                if (game is null)
                {
                    Console.WriteLine($"[HOST] WARNING — game_launch succeeded (uid={result.Uid}) but no matching Game was ever found via GetByUid after 5s of retrying. The server may not have broadcast game_info for this game, or it broadcast it before we started looking. Staging screen will not appear.");
                    return;
                }

                _uiQ.Post(() => new GameStagingScreen(_gui, _gs, _ms, _gls, _uiQ, _us).Show(game));
            }
            catch (TimeoutException)
            {
                Console.WriteLine("[HOST] TIMEOUT: server did not send game_launch within 30s — map name may not be recognized by server, or server rejected the host request");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HOST] game_launch error: {ex.GetType().Name}: {ex.Message}");
            }
        });

        _gui.Remove(_win!);
        _win!.Dispose();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void Row(TGUI.Container p, string lbl, float x, ref float y,
        float lw, float fw, float rh, float gap, out TGUI.EditBox box, string ph = "")
    {
        Lbl(p, lbl, x, y + 5f, lw);
        box = EB(ph); Place(box, x + lw + 4f, y, fw, rh); p.Add(box);
        y += rh + gap;
    }

    private static TGUI.Label InfoLbl(TGUI.Container p, float x, float y, float w)
    {
        var l = new TGUI.Label(); l.TextSize = 11;
        l.Position = new TGUI.Vector2f(x, y); l.Size = new TGUI.Vector2f(w, 17f);
        l.Renderer.SetProperty("TextColor", Theme.Rgb(150, 158, 175)); p.Add(l); return l;
    }

    private static void SectionLbl(TGUI.Container p, string t, float x, float y)
    {
        var l = new TGUI.Label(); l.Text = t; l.TextSize = 11;
        l.Position = new TGUI.Vector2f(x, y);
        l.Renderer.SetProperty("TextColor", Theme.Rgb(128, 172, 232));
        l.Renderer.SetProperty("TextStyle", "Bold");
        p.Add(l);
    }

    private static void Lbl(TGUI.Container p, string t, float x, float y, float w)
    {
        var l = new TGUI.Label(); l.Text = t; l.TextSize = 12;
        l.Position = new TGUI.Vector2f(x, y); l.Size = new TGUI.Vector2f(w, 20f);
        l.Renderer.SetProperty("TextColor", Theme.Rgb(152, 160, 178)); p.Add(l);
    }

    private static TGUI.EditBox EB(string ph)
    {
        var e = new TGUI.EditBox(); e.TextSize = 12;
        if (ph.Length > 0) e.DefaultText = ph;
        Theme.ApplyEditBox(e.Renderer); return e;
    }

    private static void Place(TGUI.Widget w, float x, float y, float width, float height)
    { w.Position = new TGUI.Vector2f(x, y); w.Size = new TGUI.Vector2f(width, height); }
}
