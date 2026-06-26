using TafClient.Domain;
using TafClient.Service;
using TafClient.UI;
using System.Collections.Generic;

namespace TafClient.UI.Widgets;

// ─── ReplaysTabWidget ────────────────────────────────────────────────────────

public sealed class ReplaysTabWidget
{
    public void Build(TGUI.Panel parent, float w, float h)
    {
        const float pad = 8f, barH = 32f;
        var search = new TGUI.EditBox();
        search.Position    = new TGUI.Vector2f(pad, pad);
        search.Size        = new TGUI.Vector2f(w - 120f - pad * 2f, barH);
        search.DefaultText = "Search by player, map, ID…";
        search.TextSize    = 12;
        Theme.ApplyEditBox(search.Renderer);
        parent.Add(search);

        var go = new TGUI.Button(); go.Text = "Search"; go.TextSize = 12;
        go.Position = new TGUI.Vector2f(w - 112f - pad, pad);
        go.Size     = new TGUI.Vector2f(112f, barH);
        Theme.ApplyPrimaryButton(go.Renderer);
        parent.Add(go);

        float lw = w - pad * 2f;
        var list = new TGUI.ListView();
        list.Position = new TGUI.Vector2f(pad, pad + barH + 6f);
        list.Size     = new TGUI.Vector2f(lw, h - pad * 2f - barH - 6f);
        list.TextSize = 12; list.HeaderTextSize = 12;
        Theme.ApplyListView(list.Renderer);
        list.ShowHorizontalGridLines = true;
        list.AddColumn("ID",      55);
        list.AddColumn("Title",   (uint)(lw * 0.34f));
        list.AddColumn("Players", 65);
        list.AddColumn("Mod",     95);
        list.AddColumn("Map",     (uint)(lw * 0.20f));
        list.AddColumn("Date",    105);
        list.AddColumn("Valid",   55);
        parent.Add(list, "ReplayList");
    }
}

// ─── LeaderboardTabWidget ────────────────────────────────────────────────────

public sealed class LeaderboardTabWidget
{
    private readonly PlayerService _ps;
    private TGUI.ListView? _list;
    public LeaderboardTabWidget(PlayerService ps) => _ps = ps;

    public void Build(TGUI.Panel parent, float w, float h)
    {
        const float pad = 8f, barH = 30f;

        var lbLbl = new TGUI.Label(); lbLbl.Text = "Leaderboard:"; lbLbl.TextSize = 13;
        lbLbl.Position = new TGUI.Vector2f(pad, pad + 5f);
        lbLbl.Renderer.SetProperty("TextColor", Theme.Rgb(158, 168, 185));
        parent.Add(lbLbl);

        var combo = new TGUI.ComboBox(); combo.TextSize = 13;
        combo.Position = new TGUI.Vector2f(110f, pad);
        combo.Size     = new TGUI.Vector2f(170f, barH);
        combo.AddItem("global"); combo.AddItem("ladder1v1"); combo.AddItem("tmm2v2");
        combo.SetSelectedItemByIndex(0);
        Theme.ApplyComboBox(combo.Renderer);
        combo.OnItemSelect += (_, _) => Repopulate();
        parent.Add(combo, "LBCombo");

        var searchBox = new TGUI.EditBox(); searchBox.TextSize = 12;
        searchBox.Position    = new TGUI.Vector2f(290f, pad);
        searchBox.Size        = new TGUI.Vector2f(200f, barH);
        searchBox.DefaultText = "Search player…";
        Theme.ApplyEditBox(searchBox.Renderer);
        parent.Add(searchBox, "LBSearch");

        float lw = w - pad * 2f;
        _list = new TGUI.ListView();
        _list.Position = new TGUI.Vector2f(pad, pad + barH + 8f);
        _list.Size     = new TGUI.Vector2f(lw, h - pad * 2f - barH - 8f);
        _list.TextSize = 13; _list.HeaderTextSize = 12;
        Theme.ApplyListView(_list.Renderer);
        _list.ShowHorizontalGridLines = true;
        _list.AddColumn("#",      45);
        _list.AddColumn("Player", (uint)(lw * 0.28f));
        _list.AddColumn("Rating", 80);
        _list.AddColumn("Mean",   72);
        _list.AddColumn("Dev",    60);
        _list.AddColumn("Games",  65);
        _list.AddColumn("Clan",   75);
        parent.Add(_list, "LBList");
        Repopulate();
    }

    private void Repopulate()
    {
        if (_list == null) return;
        _list.RemoveAllItems();
        var ranked = _ps.Players.Values
            .Where(p => p.LeaderboardRatings.ContainsKey("global"))
            .OrderByDescending(p => p.LeaderboardRatings["global"].Mean - 3 * p.LeaderboardRatings["global"].Deviation)
            .Take(200).ToList();
        for (int i = 0; i < ranked.Count; i++)
        {
            var p = ranked[i]; var r = p.LeaderboardRatings["global"];
            int rat = (int)(r.Mean - 3 * r.Deviation);
            _list.AddItem(new[] { (i+1).ToString(), p.Alias.Length > 0 ? p.Alias : p.Username,
                rat.ToString(), ((int)r.Mean).ToString(), ((int)r.Deviation).ToString(),
                r.NumberOfGames.ToString(), p.Clan });
        }
    }
}

// ─── SettingsTabWidget ───────────────────────────────────────────────────────
// Port of SettingsController.java
// Key field: gameLocationTableView (TableView<TotalAnnihilationPrefs>) with columns:
//   Mod | Executable path | Command line options
// One row per KnownFeaturedMod (tacc, taesc, tazero, tamayhem, tavmod, tatw, coop)
// Mirrors Preferences.totalAnnihilation (ListProperty<TotalAnnihilationPrefs>)

public sealed class SettingsTabWidget
{
    private readonly UserService        _us;
    private readonly TGUI.Gui           _gui;
    private readonly UiThreadQueue      _uiQ;
    private readonly TafClient.Service.PreferencesService _prefs;
    private readonly TafClient.Service.MapService         _ms;

    // Mirrors KnownFeaturedMod enum exactly (Java source: KnownFeaturedMod.java)
    // DEFAULT = tacc. All 8 mods in enum declaration order.
    private static readonly (string Technical, string Display)[] KnownMods =
    [
        ("tacc",      "Total Annihilation (tacc)"),
        ("taesc",     "TA: Escalation (taesc)"),
        ("tazero",    "TA: Zero (tazero)"),
        ("tamayhem",  "TA: Mayhem (tamayhem)"),
        ("tavmod",    "TA: VMod Custom (tavmod)"),
        ("tatw",      "TA: Total War (tatw)"),
        ("coop",      "Co-op (coop)"),
        ("ladder1v1", "Ladder 1v1 (ladder1v1)"),
    ];

    // Stores current exe path per mod — mirrors TotalAnnihilationPrefs.installedExePath
    private readonly Dictionary<string, string> _exePaths    = new();
    private readonly Dictionary<string, string> _cmdOptions  = new();
    // EditBox references so Save can read them
    private readonly List<(string mod, TGUI.EditBox pathBox, TGUI.EditBox cmdBox)> _rows = new();

    public SettingsTabWidget(UserService us, TGUI.Gui gui, UiThreadQueue uiQ,
                             TafClient.Service.PreferencesService prefs,
                             TafClient.Service.MapService ms)
    { _us = us; _gui = gui; _uiQ = uiQ; _prefs = prefs; _ms = ms; }

    public void Build(TGUI.Panel parent, float w, float h)
    {
        var scroll = new TGUI.ScrollablePanel();
        scroll.Position = new TGUI.Vector2f(0f, 0f);
        scroll.Size     = new TGUI.Vector2f(w, h);
        scroll.Renderer.SetProperty("BackgroundColor", Theme.Rgb(20, 22, 28));
        scroll.Renderer.SetProperty("Borders",         "0");
        parent.Add(scroll, "SettingsScroll");

        const float pad = 22f, lw = 135f, fw = 260f, rh = 28f, gap = 8f;
        float x = pad, y = pad;

        // ── Account ───────────────────────────────────────────────────────────
        Section(scroll, "Account", x, w, ref y);
        AddRow(scroll, "Username:", _us.Username ?? "—",
            x, ref y, lw, fw, rh, gap, ro: true);
        y += 4f;

        // ── Game executable paths (one per mod) ───────────────────────────────
        // This is the port of gameLocationTableView with columns Mod / Executable / CMD options
        Section(scroll, "Game Executable Paths", x, w, ref y);

        // ── Wine detection (macOS / Linux only) ───────────────────────────────
        if (TafClient.Service.WineDetector.NeedsWine)
        {
            string wineStatus = TafClient.Service.WineDetector.StatusString();
            string? wineVer   = TafClient.Service.WineDetector.GetWineVersion();
            string winePrefix = TafClient.Service.WineDetector.GetWinePrefix();
            bool wineOk       = TafClient.Service.WineDetector.WineAvailable;

            var wineLbl = new TGUI.Label();
            wineLbl.Text     = "Wine:";
            wineLbl.TextSize = 12;
            wineLbl.Position = new TGUI.Vector2f(x, y + 5f);
            wineLbl.Renderer.SetProperty("TextColor", Theme.Rgb(155, 162, 180));
            scroll.Add(wineLbl);

            var wineVal = new TGUI.Label();
            wineVal.Text     = wineVer is not null ? $"{wineStatus}  ({wineVer})" : wineStatus;
            wineVal.TextSize = 12;
            wineVal.Position = new TGUI.Vector2f(x + 52f, y + 5f);
            wineVal.Renderer.SetProperty("TextColor",
                wineOk ? Theme.Rgb(100, 200, 110) : Theme.Rgb(220, 90, 80));
            scroll.Add(wineVal);
            y += rh + gap;

            var prefixLbl = new TGUI.Label();
            prefixLbl.Text     = "WINEPREFIX:";
            prefixLbl.TextSize = 11;
            prefixLbl.Position = new TGUI.Vector2f(x, y + 5f);
            prefixLbl.Renderer.SetProperty("TextColor", Theme.Rgb(130, 138, 155));
            scroll.Add(prefixLbl);

            var prefixVal = new TGUI.Label();
            prefixVal.Text     = winePrefix;
            prefixVal.TextSize = 11;
            prefixVal.Position = new TGUI.Vector2f(x + 90f, y + 5f);
            prefixVal.Renderer.SetProperty("TextColor", Theme.Rgb(160, 168, 188));
            scroll.Add(prefixVal);
            y += rh + gap;

            // Wine prefix setup status + re-run button
            var setup = new TafClient.Service.WinePrefixSetup(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<TafClient.Service.WinePrefixSetup>.Instance);
            bool setupDone = setup.IsComplete;

            var setupLbl = new TGUI.Label();
            setupLbl.Text     = "Prefix setup:";
            setupLbl.TextSize = 11;
            setupLbl.Position = new TGUI.Vector2f(x, y + 5f);
            setupLbl.Renderer.SetProperty("TextColor", Theme.Rgb(130, 138, 155));
            scroll.Add(setupLbl);

            var setupVal = new TGUI.Label();
            setupVal.Text     = setupDone ? "✓ Complete" : "Not run yet";
            setupVal.TextSize = 11;
            setupVal.Position = new TGUI.Vector2f(x + 90f, y + 5f);
            setupVal.Renderer.SetProperty("TextColor",
                setupDone ? Theme.Rgb(100, 200, 110) : Theme.Rgb(190, 150, 60));
            scroll.Add(setupVal);

            var rerunBtn = new TGUI.Button();
            rerunBtn.Text     = setupDone ? "Re-run Setup" : "Run Setup Now";
            rerunBtn.TextSize = 11;
            rerunBtn.Position = new TGUI.Vector2f(x + 90f + 90f, y);
            rerunBtn.Size     = new TGUI.Vector2f(115f, rh);
            Theme.ApplySecondaryButton(rerunBtn.Renderer);
            rerunBtn.OnPress += (_, _) =>
            {
                // Delete sentinel to force re-run, then re-run setup in background
                var s2 = new TafClient.Service.WinePrefixSetup(
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<TafClient.Service.WinePrefixSetup>.Instance);
                try { File.Delete(s2.SentinelFile); } catch { }
                rerunBtn.Enabled = false;
                setupVal.Text = "Running…";
                setupVal.Renderer.SetProperty("TextColor", Theme.Rgb(190, 190, 60));
                _ = Task.Run(async () =>
                {
                    bool ok = await s2.SetupAsync();
                    // Update UI on next available frame via UiThreadQueue not available here —
                    // use a simple callback approach
                    setupVal.Text = ok ? "✓ Complete" : "Failed — check console";
                    setupVal.Renderer.SetProperty("TextColor",
                        ok ? Theme.Rgb(100, 200, 110) : Theme.Rgb(220, 90, 80));
                    rerunBtn.Enabled = true;
                });
            };
            scroll.Add(rerunBtn);
            y += rh + gap;

            if (!wineOk)
            {
                var hint = new TGUI.Label();
                hint.Text     = OperatingSystem.IsMacOS()
                    ? "Install CrossOver, Whisky, or Homebrew wine to play TA."
                    : "Install Wine: sudo apt install wine  or  sudo pacman -S wine";
                hint.TextSize = 11;
                hint.Position = new TGUI.Vector2f(x, y);
                hint.Renderer.SetProperty("TextColor", Theme.Rgb(190, 150, 60));
                scroll.Add(hint);
                y += 20f + gap;
            }

            // Install Mod — copies a mod folder into the Wine prefix's
            // drive_c (~/.wine-taf/drive_c/TAF/{mod}) so Wine sees a normal
            // C:\TAF\{mod}\TotalA.exe path when launching, exactly as it
            // would on real Windows.
            var installModBtn = new TGUI.Button();
            installModBtn.Text     = "Install Mod…";
            installModBtn.TextSize = 12;
            installModBtn.Position = new TGUI.Vector2f(x, y);
            installModBtn.Size     = new TGUI.Vector2f(160f, rh);
            Theme.ApplyPrimaryButton(installModBtn.Renderer);
            installModBtn.OnPress += (_, _) =>
                InstallModDialog.Show(_gui, _uiQ, _prefs, _ms);
            scroll.Add(installModBtn);
            y += rh + gap;

            y += 4f;
        }

        AddModTableHeader(scroll, x, ref y, w - pad * 2f, rh);

        foreach (var (tech, display) in KnownMods)
        {
            AddModRow(scroll, tech, display, x, ref y, w - pad * 2f, rh, gap);
        }
        y += 6f;

        // ── Chat ──────────────────────────────────────────────────────────────
        Section(scroll, "Chat", x, w, ref y);
        AddCheck(scroll, "Enable IRC integration",    x, ref y, true);
        AddCheck(scroll, "Show foe messages in chat", x, ref y, false);
        y += 4f;

        // ── Notifications ─────────────────────────────────────────────────────
        Section(scroll, "Notifications", x, w, ref y);
        AddCheck(scroll, "Enable notification sounds",       x, ref y, true);
        AddCheck(scroll, "Friend comes online",              x, ref y, true);
        AddCheck(scroll, "Friend goes offline",              x, ref y, false);
        AddCheck(scroll, "Friend joins a game",              x, ref y, true);
        AddCheck(scroll, "Display friend online toast",      x, ref y, true);
        AddCheck(scroll, "Display friend offline toast",     x, ref y, false);
        y += 4f;

        // ── Gameplay ──────────────────────────────────────────────────────────
        Section(scroll, "Gameplay", x, w, ref y);
        AddCheck(scroll, "Auto-launch on host",              x, ref y, false);
        AddCheck(scroll, "Auto-launch on join",              x, ref y, true);
        AddCheck(scroll, "Auto-rehost",                      x, ref y, false);
        AddCheck(scroll, "Auto team balance",                x, ref y, true);
        AddCheck(scroll, "Sequenced launch",                 x, ref y, false);
        AddCheck(scroll, "Show password-protected games",    x, ref y, true);
        y += 4f;

        // ── Replay ────────────────────────────────────────────────────────────
        Section(scroll, "Replay", x, w, ref y);
        AddRow(scroll, "Watch delay (s):", "300", x, ref y, lw, 80f, rh, gap);
        AddCheck(scroll, "Suppress replay chat",             x, ref y, false);
        y += 14f;

        // ── Save / Reset ─────────────────────────────────────────────────────
        var save = new TGUI.Button(); save.Text = "Save Settings"; save.TextSize = 13;
        save.Position = new TGUI.Vector2f(x, y);
        save.Size     = new TGUI.Vector2f(155f, rh + 4f);
        Theme.ApplyPrimaryButton(save.Renderer);
        save.OnPress += (_, _) => OnSave();
        scroll.Add(save);

        var reset = new TGUI.Button(); reset.Text = "Reset"; reset.TextSize = 13;
        reset.Position = new TGUI.Vector2f(x + 163f, y);
        reset.Size     = new TGUI.Vector2f(88f, rh + 4f);
        Theme.ApplySecondaryButton(reset.Renderer);
        scroll.Add(reset);
    }

    // ── Mod table header ──────────────────────────────────────────────────────

    private static void AddModTableHeader(TGUI.Container p, float x, ref float y, float tw, float rh)
    {
        const float modW = 120f, cmdW = 150f, browseW = 34f, regW = 90f;
        float exeW = tw - modW - cmdW - browseW - regW - 20f;

        // Column headers
        var h1 = MkLabel("Mod",              Theme.Rgb(140, 150, 175)); h1.TextSize = 11;
        h1.Position = new TGUI.Vector2f(x, y); h1.Size = new TGUI.Vector2f(modW, rh);
        p.Add(h1);

        var h2 = MkLabel("Executable path",  Theme.Rgb(140, 150, 175)); h2.TextSize = 11;
        h2.Position = new TGUI.Vector2f(x + modW + 4f, y); h2.Size = new TGUI.Vector2f(exeW, rh);
        p.Add(h2);

        var h3 = MkLabel("Command line opts",Theme.Rgb(140, 150, 175)); h3.TextSize = 11;
        h3.Position = new TGUI.Vector2f(x + modW + 4f + exeW + browseW + 6f, y); h3.Size = new TGUI.Vector2f(cmdW, rh);
        p.Add(h3);

        y += rh + 2f;

        // Separator under header
        var sep = new TGUI.SeparatorLine();
        sep.Position = new TGUI.Vector2f(x, y);
        sep.Size     = new TGUI.Vector2f(tw, 1f);
        sep.Renderer.SetProperty("Color", Theme.Rgb(52, 58, 74));
        p.Add(sep);
        y += 6f;
    }

    // ── One mod row: [Mod label] [Exe editbox] [Browse btn] [Cmd editbox] ────

    private void AddModRow(TGUI.Container p, string tech, string display,
        float x, ref float y, float tw, float rh, float gap)
    {
        const float modW = 120f, cmdW = 150f, browseW = 34f, regW = 90f;
        float exeW = tw - modW - cmdW - browseW - regW - 20f;

        // Mod name label
        var lbl = MkLabel(display, Theme.Rgb(195, 200, 215)); lbl.TextSize = 12;
        lbl.Position = new TGUI.Vector2f(x, y + 5f);
        lbl.Size     = new TGUI.Vector2f(modW, rh);
        p.Add(lbl);

        // Executable path EditBox — load saved value from PreferencesService
        var pathBox = new TGUI.EditBox();
        pathBox.Position    = new TGUI.Vector2f(x + modW + 4f, y);
        pathBox.Size        = new TGUI.Vector2f(exeW, rh);
        pathBox.TextSize    = 11;
        pathBox.DefaultText = "Path to TotalA.exe…";
        string savedPath = _prefs.GetMod(tech).ExePath;
        if (!string.IsNullOrEmpty(savedPath)) pathBox.Text = savedPath;
        Theme.ApplyEditBox(pathBox.Renderer);
        p.Add(pathBox);

        // Browse "…" button
        var browse = new TGUI.Button(); browse.Text = "…"; browse.TextSize = 13;
        browse.Position = new TGUI.Vector2f(x + modW + 4f + exeW + 2f, y);
        browse.Size     = new TGUI.Vector2f(browseW, rh);
        Theme.ApplySecondaryButton(browse.Renderer);
        var capturedPath = pathBox;
        var capturedTech = tech;
        browse.OnPress += (_, _) =>
            ExePathDialog.Show(
                _gui,
                $"Select executable for {display}",
                capturedPath.Text.Length > 0 ? capturedPath.Text : null,
                _uiQ,
                path => capturedPath.Text = path);
        p.Add(browse);

        // Command line options EditBox — load saved value from PreferencesService
        var cmdBox = new TGUI.EditBox();
        cmdBox.Position    = new TGUI.Vector2f(x + modW + 4f + exeW + browseW + 6f, y);
        cmdBox.Size        = new TGUI.Vector2f(cmdW, rh);
        cmdBox.TextSize    = 11;
        cmdBox.DefaultText = "Optional args…";
        string savedCmd = _prefs.GetMod(tech).CmdOptions;
        if (!string.IsNullOrEmpty(savedCmd)) cmdBox.Text = savedCmd;
        Theme.ApplyEditBox(cmdBox.Renderer);
        p.Add(cmdBox);

        // Generate .reg button — writes a DirectPlay registration .reg file
        // for this mod next to its exe, using the saved path/exe/args. The
        // user double-clicks the resulting file and approves the normal
        // Windows UAC prompt to actually apply it — this is the standalone
        // alternative to --registerdplay's own (broken-under-Wine, and
        // heavyweight even on real Windows) elevation path.
        var regBtn = new TGUI.Button(); regBtn.Text = "Gen .reg"; regBtn.TextSize = 11;
        regBtn.Position = new TGUI.Vector2f(x + modW + 4f + exeW + browseW + cmdW + 8f, y);
        regBtn.Size     = new TGUI.Vector2f(regW, rh);
        Theme.ApplySecondaryButton(regBtn.Renderer);
        var capturedCmd = cmdBox;
        regBtn.OnPress += (_, _) => GenerateRegFile(capturedTech, display, capturedPath, capturedCmd);
        p.Add(regBtn);

        _rows.Add((tech, pathBox, cmdBox));
        y += rh + gap;
    }

    /// <summary>
    /// Writes a {mod}.reg file next to the mod's configured exe path (falls
    /// back to the app directory if no path is set yet), using whatever is
    /// currently in the path/args boxes — including unsaved edits, so the
    /// user doesn't have to click Save first just to generate this.
    /// </summary>
    private void GenerateRegFile(string tech, string display, TGUI.EditBox pathBox, TGUI.EditBox cmdBox)
    {
        string exePath = pathBox.Text.Trim();
        if (string.IsNullOrEmpty(exePath))
        {
            Console.WriteLine($"[SETTINGS] Cannot generate .reg for {tech} — no exe path set");
            return;
        }

        string gamePath = Path.GetDirectoryName(exePath) ?? "";
        string gameExe  = Path.GetFileName(exePath);
        if (string.IsNullOrEmpty(gamePath) || string.IsNullOrEmpty(gameExe))
        {
            Console.WriteLine($"[SETTINGS] Cannot generate .reg for {tech} — could not split path '{exePath}'");
            return;
        }

        string outputPath = Path.Combine(gamePath, $"{tech}_directplay.reg");
        try
        {
            DPlayRegFileGenerator.WriteRegFile(outputPath, tech, gamePath, gameExe, cmdBox.Text.Trim());
            Console.WriteLine($"[SETTINGS] Wrote {outputPath} — double-click it and approve the UAC prompt to register {display}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SETTINGS] Failed to write .reg for {tech}: {ex.Message}");
        }
    }

    // ── Save: collect all mod paths ───────────────────────────────────────────

    private void OnSave()
    {
        foreach (var (mod, pathBox, cmdBox) in _rows)
        {
            _prefs.SetModExePath(mod, pathBox.Text.Trim());
            _prefs.SetModCmdOptions(mod, cmdBox.Text.Trim());
        }
        // Re-scan maps for each mod whose path was updated
        // Mirrors MapService listener on installedExePathProperty
        foreach (var (mod, _, _) in _rows)
            _ms.RefreshMod(mod);

        Console.WriteLine($"[SETTINGS] Saved to {TafClient.Service.PreferencesService.PrefsFilePath}");
    }

    // ── Generic helpers ───────────────────────────────────────────────────────

    private static void Section(TGUI.Container p, string t, float x, float w, ref float y)
    {
        var l = new TGUI.Label(); l.Text = t; l.TextSize = 14;
        l.Position = new TGUI.Vector2f(x, y);
        l.Renderer.SetProperty("TextColor", Theme.Rgb(128, 172, 232));
        l.Renderer.SetProperty("TextStyle", "Bold");
        p.Add(l); y += 22f;

        var s = new TGUI.SeparatorLine();
        s.Position = new TGUI.Vector2f(x, y);
        s.Size     = new TGUI.Vector2f(w - x * 2f, 1f);
        s.Renderer.SetProperty("Color", Theme.Rgb(50, 56, 74));
        p.Add(s); y += 10f;
    }

    private static void AddRow(TGUI.Container p, string lbl, string val,
        float x, ref float y, float lw, float fw, float rh, float gap,
        bool ro = false, string ph = "")
    {
        var l = new TGUI.Label(); l.Text = lbl; l.TextSize = 12;
        l.Position = new TGUI.Vector2f(x, y + 7f); l.Size = new TGUI.Vector2f(lw, rh);
        l.Renderer.SetProperty("TextColor", Theme.Rgb(155, 162, 180)); p.Add(l);

        var b = new TGUI.EditBox(); b.Text = val; b.TextSize = 12; b.ReadOnly = ro;
        b.Position = new TGUI.Vector2f(x + lw + 8f, y); b.Size = new TGUI.Vector2f(fw, rh);
        if (ph.Length > 0) b.DefaultText = ph;
        Theme.ApplyEditBox(b.Renderer); p.Add(b);
        y += rh + gap;
    }

    private static void AddCheck(TGUI.Container p, string lbl, float x, ref float y, bool on)
    {
        var cb = new TGUI.CheckBox(); cb.Text = lbl; cb.TextSize = 13; cb.Checked = on;
        cb.Position = new TGUI.Vector2f(x, y); cb.Size = new TGUI.Vector2f(22f, 22f);
        Theme.ApplyCheckBox(cb.Renderer); p.Add(cb);
        y += 29f;
    }

    private static TGUI.Label MkLabel(string t, string color)
    {
        var l = new TGUI.Label(); l.Text = t;
        l.Renderer.SetProperty("TextColor", color);
        return l;
    }
}
