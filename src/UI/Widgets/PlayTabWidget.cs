using TafClient.Domain;
using TafClient.Net.Domain;
using TafClient.Service;
using TafClient.UI;

namespace TafClient.UI.Widgets;

public sealed class PlayTabWidget
{
    private readonly GameService   _gs;
    private readonly PlayerService _ps;
    private readonly TGUI.Gui      _gui;
    private readonly UiThreadQueue _uiQ;
    private readonly MapService    _ms;
    private readonly PreferencesService _prefs;
    private readonly UserService   _us;
    private readonly GameLaunchService _gls;

    private TGUI.ListView? _list;
    private TGUI.Label?    _dTitle, _dHost, _dMap, _dPlayers, _dMod, _dRating, _dStatus, _dPwd;
    private TGUI.Button?   _joinBtn;
    private TGUI.Label?    _joinStatus;
    private Game? _sel;
    private readonly Dictionary<int, int> _idx = new();  // gameId → row index

    public PlayTabWidget(GameService gs, PlayerService ps, TGUI.Gui gui, UiThreadQueue uiQ, MapService ms, PreferencesService prefs, UserService us, GameLaunchService gls)
    { _gs = gs; _ps = ps; _gui = gui; _uiQ = uiQ; _ms = ms; _prefs = prefs; _us = us; _gls = gls; }

    public void Build(TGUI.Panel parent, float w, float h)
    {
        const float pad = 8f, split = 0.67f;
        float lw = w * split - pad * 1.5f;
        float dw = w * (1f - split) - pad * 1.5f;
        float ih = h - pad * 2f;
        const float fh = 30f;

        // Filter bar
        var filter = W<TGUI.EditBox>(); filter.DefaultText = "Filter games…"; filter.TextSize = 12;
        filter.Position = new TGUI.Vector2f(pad, pad);
        filter.Size     = new TGUI.Vector2f(lw - 100f, fh);
        Theme.ApplyEditBox(filter.Renderer);
        filter.OnTextChange += (_, _) => ApplyFilter(filter.Text);
        parent.Add(filter);

        var hostBtn2 = W<TGUI.Button>(); hostBtn2.Text = "Host Game"; hostBtn2.TextSize = 12;
        hostBtn2.Position = new TGUI.Vector2f(pad + lw - 94f, pad);
        hostBtn2.Size     = new TGUI.Vector2f(94f, fh);
        Theme.ApplyPrimaryButton(hostBtn2.Renderer);
        hostBtn2.OnPress += (_, _) => new HostGameDialog(_gui, _gs, _ms, _uiQ, _prefs, _us, _gls).Show();
        parent.Add(hostBtn2);

        // Game list
        _list = W<TGUI.ListView>();
        _list.Position = new TGUI.Vector2f(pad, pad + fh + 6f);
        _list.Size     = new TGUI.Vector2f(lw, ih - fh - 6f);
        _list.TextSize = 12; _list.HeaderTextSize = 12;
        Theme.ApplyListView(_list.Renderer);
        _list.ShowHorizontalGridLines = true;
        _list.AddColumn("Title",   (uint)(lw * 0.32f));
        _list.AddColumn("Players", (uint)(lw * 0.11f));
        _list.AddColumn("Map",     (uint)(lw * 0.23f));
        _list.AddColumn("Mod",     (uint)(lw * 0.16f));
        _list.AddColumn("Rating",  (uint)(lw * 0.10f));
        _list.AddColumn("Status",  (uint)(lw * 0.08f));
        _list.OnItemSelect += (_, a) => OnSelect(a.Index);
        parent.Add(_list);

        // Detail panel
        var dp = W<TGUI.Panel>();
        dp.Position = new TGUI.Vector2f(pad + lw + pad, pad);
        dp.Size     = new TGUI.Vector2f(dw, ih);
        dp.Renderer.SetProperty("BackgroundColor", Theme.Rgb(27, 30, 40));
        dp.Renderer.SetProperty("BorderColor",     Theme.Rgb(48, 52, 65));
        dp.Renderer.SetProperty("Borders",         "1");
        parent.Add(dp);

        float y = 14f;
        _dTitle = DLabel(dp, "Select a game", 12f, y, dw - 24f, 16, true); y += 28f;
        Sep(dp, 12f, y, dw - 24f); y += 12f;
        _dHost    = DRow(dp, "Host:",    12f, ref y, dw);
        _dMap     = DRow(dp, "Map:",     12f, ref y, dw);
        _dPlayers = DRow(dp, "Players:", 12f, ref y, dw);
        _dMod     = DRow(dp, "Mod:",     12f, ref y, dw);
        _dRating  = DRow(dp, "Rating:",  12f, ref y, dw);
        _dStatus  = DRow(dp, "Status:",  12f, ref y, dw);
        _dPwd     = DRow(dp, "Password:",12f, ref y, dw);
        y += 8f;

        _joinBtn = W<TGUI.Button>(); _joinBtn.Text = "Join Game"; _joinBtn.TextSize = 13;
        _joinBtn.Position = new TGUI.Vector2f(12f, y);
        _joinBtn.Size     = new TGUI.Vector2f(dw - 24f, 32f);
        _joinBtn.Enabled  = false;
        Theme.ApplyPrimaryButton(_joinBtn.Renderer);
        _joinBtn.OnPress += (_, _) => DoJoin();
        dp.Add(_joinBtn);

        y += 38f;
        _joinStatus = new TGUI.Label();
        _joinStatus.TextSize = 11;
        _joinStatus.Position = new TGUI.Vector2f(12f, y);
        _joinStatus.Size     = new TGUI.Vector2f(dw - 24f, 32f);
        _joinStatus.Renderer.SetProperty("TextColor", Theme.Rgb(158, 165, 185));
        dp.Add(_joinStatus);

        // Live subscription — always via UI queue
        _gs.GameAdded.Subscribe(g   => _uiQ.Post(() => AddRow(g)));
        _gs.GameUpdated.Subscribe(g => _uiQ.Post(() => UpdateRow(g)));
        _gs.GameRemoved.Subscribe(g => _uiQ.Post(() => RemoveRow(g)));
        // Auto-select the current game when hosting or joining
        _gs.CurrentGameChanged.Subscribe(g => _uiQ.Post(() =>
        {
            if (g is null) { ClearDetail(); return; }
            // Select the row in the list and show detail
            // SelectedItemIndex is read-only in TGUI.Net — select by clicking is user-driven.
            // We can still populate the detail panel for the current game directly.
            _sel = g;
            Populate(g);
        }));
        // Populate existing games
        foreach (var g in _gs.Games) AddRow(g);
    }

    // ── Row ops ───────────────────────────────────────────────────────────────

    private void AddRow(Game g)
    {
        if (_list == null) return;
        if (_idx.ContainsKey(g.Id)) { UpdateRow(g); return; }
        _list.AddItem(Row(g));
        _idx[g.Id] = (int)_list.ItemCount - 1;
    }

    private void UpdateRow(Game g)
    {
        if (_list == null || !_idx.TryGetValue(g.Id, out int i)) return;
        _list.ChangeItem(i, Row(g));
        if (_sel?.Id == g.Id) Populate(g);
    }

    private void RemoveRow(Game g)
    {
        if (_list == null || !_idx.TryGetValue(g.Id, out int i)) return;
        _list.RemoveItem(i);
        _idx.Remove(g.Id);
        foreach (var k in _idx.Keys.ToList()) if (_idx[k] > i) _idx[k]--;
        if (_sel?.Id == g.Id) ClearDetail();
    }

    private void ApplyFilter(string text)
    {
        // Rebuild list with filter applied
        if (_list == null) return;
        _list.RemoveAllItems();
        _idx.Clear();
        foreach (var g in _gs.Games)
        {
            if (string.IsNullOrEmpty(text) ||
                g.Title.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                g.MapName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                g.Host.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                _list.AddItem(Row(g));
                _idx[g.Id] = (int)_list.ItemCount - 1;
            }
        }
    }

    private void OnSelect(int i)
    {
        if (i < 0) { ClearDetail(); return; }
        _sel = _gs.Games.FirstOrDefault(g => _idx.TryGetValue(g.Id, out int ri) && ri == i);
        if (_sel != null) Populate(_sel);
    }

    private void DoJoin()
    {
        if (_sel == null)
        {
            Console.WriteLine("[JOIN] Join pressed but no game selected — button should have been disabled");
            if (_joinStatus != null) _joinStatus.Text = "No game selected.";
            return;
        }

        var game = _sel;
        Console.WriteLine($"[JOIN] Joining game id={game.Id} title='{game.Title}' map={game.MapName} archive={game.MapArchiveName}");
        if (_joinBtn != null)    { _joinBtn.Enabled = false; _joinBtn.Text = "Joining…"; }
        if (_joinStatus != null) _joinStatus.Text = "Checking map, connecting to server…";

        // Show the staging screen using the Game object we already hold
        // (the one the user selected from the list) directly — no need to
        // wait on CurrentGameChanged at all here, since unlike the host
        // flow we don't even need a fresh lookup; this IS the same Game
        // reference that GameUpdated will keep refreshing in place.

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _gs.JoinGameAsync(game.Id, null).WaitAsync(TimeSpan.FromSeconds(30));
                _uiQ.Post(() =>
                {
                    if (result is null)
                    {
                        Console.WriteLine("[JOIN] Server returned null game_launch — join was rejected or game no longer exists");
                        if (_joinStatus != null) _joinStatus.Text = "✗ Could not join — game may have ended.";
                    }
                    else
                    {
                        Console.WriteLine($"[JOIN] game_launch received: uid={result.Uid} map={result.Mapname} mod={result.Mod}");
                        if (_joinStatus != null) _joinStatus.Text = "✓ Launching game…";
                        new GameStagingScreen(_gui, _gs, _ms, _gls, _uiQ, _us).Show(game);
                    }
                    if (_joinBtn != null) { _joinBtn.Enabled = _sel?.IsOpen() ?? false; _joinBtn.Text = "Join Game"; }
                });
            }
            catch (TimeoutException)
            {
                Console.WriteLine("[JOIN] TIMEOUT: no response within 30s (map download may have stalled, or server did not respond)");
                _uiQ.Post(() =>
                {
                    if (_joinStatus != null) _joinStatus.Text = "✗ Timed out — check your connection or map download.";
                    if (_joinBtn != null) { _joinBtn.Enabled = _sel?.IsOpen() ?? false; _joinBtn.Text = "Join Game"; }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JOIN] ERROR: {ex.GetType().Name}: {ex.Message}");
                _uiQ.Post(() =>
                {
                    if (_joinStatus != null) _joinStatus.Text = $"✗ Error: {ex.Message}";
                    if (_joinBtn != null) { _joinBtn.Enabled = _sel?.IsOpen() ?? false; _joinBtn.Text = "Join Game"; }
                });
            }
        });
    }

    private void Populate(Game g)
    {
        if (_dTitle != null) _dTitle.Text    = g.Title;
        if (_dHost != null)  _dHost.Text     = g.Host;
        if (_dMap != null)   _dMap.Text      = g.MapName;
        if (_dPlayers != null) _dPlayers.Text = $"{g.NumPlayers}/{g.MaxPlayers}";
        if (_dMod != null)   _dMod.Text      = g.FeaturedMod;
        if (_dRating != null) _dRating.Text  = RatingStr(g);
        if (_dStatus != null) _dStatus.Text  = StatusStr(g.Status);
        if (_dPwd != null)   _dPwd.Text      = g.PasswordProtected ? "🔒 Yes" : "Open";
        if (_joinBtn != null) _joinBtn.Enabled = g.IsOpen();
    }

    private void ClearDetail()
    {
        _sel = null;
        if (_dTitle != null) _dTitle.Text = "Select a game";
        foreach (var l in new[] { _dHost, _dMap, _dPlayers, _dMod, _dRating, _dStatus, _dPwd })
            if (l != null) l.Text = "—";
        if (_joinBtn != null) _joinBtn.Enabled = false;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string[] Row(Game g) =>
    [
        Trunc(g.Title, 34),
        $"{g.NumPlayers}/{g.MaxPlayers}",
        Trunc(g.MapName, 20),
        g.FeaturedMod,
        RatingStr(g),
        StatusStr(g.Status),
    ];

    private static string Trunc(string s, int max) =>
        s.Length > max ? s[..(max - 1)] + "…" : s;

    private static string RatingStr(Game g) =>
        (g.MinRating.HasValue, g.MaxRating.HasValue) switch
        {
            (true, true)   => $"{g.MinRating}–{g.MaxRating}",
            (true, false)  => $"{g.MinRating}+",
            (false, true)  => $"<{g.MaxRating}",
            _              => "Any"
        };

    private static string StatusStr(GameStatus s) => s switch
    {
        GameStatus.Staging    => "Open",
        GameStatus.Battleroom => "Lobby",
        GameStatus.Launching  => "Starting",
        GameStatus.Live       => "Live",
        GameStatus.Ended      => "Done",
        _                     => "?"
    };

    private static T W<T>() where T : new() => new T();

    private static void Sep(TGUI.Container p, float x, float y, float w)
    {
        var s = new TGUI.SeparatorLine();
        s.Position = new TGUI.Vector2f(x, y);
        s.Size     = new TGUI.Vector2f(w, 1f);
        s.Renderer.SetProperty("Color", Theme.Rgb(50, 55, 72));
        p.Add(s);
    }

    private static TGUI.Label DLabel(TGUI.Container p, string t,
        float x, float y, float w, uint sz, bool bold)
    {
        var l = new TGUI.Label();
        l.Text = t; l.TextSize = (int)sz;
        l.Position = new TGUI.Vector2f(x, y);
        l.Size     = new TGUI.Vector2f(w, 20f);
        l.Renderer.SetProperty("TextColor", bold ? Theme.Rgb(205, 215, 255) : Theme.Rgb(185, 192, 210));
        if (bold) l.Renderer.SetProperty("TextStyle", "Bold");
        p.Add(l); return l;
    }

    private static TGUI.Label DRow(TGUI.Container p, string key,
        float x, ref float y, float panelW)
    {
        const float kw = 70f, rh = 20f, gap = 5f;
        var k = new TGUI.Label(); k.Text = key; k.TextSize = 11;
        k.Position = new TGUI.Vector2f(x, y); k.Size = new TGUI.Vector2f(kw, rh);
        k.Renderer.SetProperty("TextColor", Theme.Rgb(115, 124, 145)); p.Add(k);

        var v = new TGUI.Label(); v.Text = "—"; v.TextSize = 12;
        v.Position = new TGUI.Vector2f(x + kw + 4f, y);
        v.Size     = new TGUI.Vector2f(panelW - x - kw - 26f, rh);
        v.Renderer.SetProperty("TextColor", Theme.Rgb(208, 214, 230)); p.Add(v);
        y += rh + gap; return v;
    }
}
