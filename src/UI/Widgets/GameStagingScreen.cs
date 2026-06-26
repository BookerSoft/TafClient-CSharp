using TafClient.Domain;
using TafClient.Net.Domain;
using TafClient.Service;
using TafClient.UI;

namespace TafClient.UI.Widgets;

/// <summary>
/// The staging/lobby room screen — shown after a successful host or join,
/// while players wait for the actual game (TotalA.exe) to open and the
/// match to be set up. Mirrors the real client's right-side panel: map
/// preview, game title/mod/status, a live-updating player/team roster, and
/// Leave/Start buttons.
///
/// This was a genuinely missing screen — confirmed against real client
/// screenshots — not just a layout tweak. Built as a self-contained overlay
/// window (same pattern as HostGameDialog) rather than restructuring
/// PlayTabWidget's main layout, since threading GameLaunchService through
/// the existing MainScreen -> PlayTabWidget constructor chain for a first
/// version carries more risk than benefit given how much has already gone
/// wrong this session from rushed wiring changes.
///
/// Live updates: GameService.GameUpdated and CurrentGameChanged already
/// fire correctly as game_info messages arrive from the server (confirmed
/// via GameService.OnGameInfo's existing CurrentGame-tracking logic) — this
/// screen just subscribes and refreshes the roster/status labels, no new
/// server-side plumbing needed.
/// </summary>
public sealed class GameStagingScreen
{
    // Tracks the currently-open staging screen across all instances, so a
    // new Show() call always closes any previous one first. Without this,
    // hosting/joining multiple times in one session (extremely common
    // during testing, and not unusual in normal play either) would leave
    // each previous instance's GameUpdated/CurrentGameChanged subscriptions
    // alive forever — those are session-wide observables that fire for
    // every game in the whole client, not just the one being staged, so a
    // leaked subscription keeps doing real work (rebuilding a roster no
    // one can see) for the rest of the process's lifetime. Confirmed as a
    // plausible source of the reported memory growth.
    private static GameStagingScreen? s_current;

    private readonly TGUI.Gui            _gui;
    private readonly GameService         _gs;
    private readonly MapService          _ms;
    private readonly GameLaunchService   _gls;
    private readonly UiThreadQueue       _uiQ;
    private readonly UserService         _us;

    private TGUI.ChildWindow? _win;
    private TGUI.Label?       _statusLabel;
    private TGUI.Label?       _playerCountLabel;
    private TGUI.Panel?       _rosterPanel;
    private TGUI.Picture?     _mapPreview;
    private IDisposable?      _updatedSub;
    private IDisposable?      _currentGameSub;
    private Game?             _game;

    public GameStagingScreen(TGUI.Gui gui, GameService gs, MapService ms, GameLaunchService gls, UiThreadQueue uiQ, UserService us)
    {
        _gui = gui; _gs = gs; _ms = ms; _gls = gls; _uiQ = uiQ; _us = us;
    }

    /// <summary>Shows the staging screen for the given game and starts listening for live updates.</summary>
    public void Show(Game game)
    {
        // Close any previous staging screen first — see s_current's doc
        // comment for why this matters.
        s_current?.Close();
        s_current = this;

        _game = game;
        const float dw = 420f, dh = 480f;

        _win = new TGUI.ChildWindow();
        _win.Title    = $"{game.Host}'s Game";
        _win.Size     = new TGUI.Vector2f(dw, dh);
        _win.Position = new TGUI.Vector2f(
            _gui.GetView().Size.X - dw - 20f,
            70f);
        _win.Resizable = false;
        Theme.ApplyChildWindow(_win.Renderer);
        // Deferred via _uiQ.Post — DoLeave() ultimately disposes _win, and
        // doing that synchronously from within this callback would be
        // running inside TGUI's own native close-signal processing
        // (ChildWindow::ProcessClosedSignal). A known TGUI.Net issue
        // (texus/TGUI.Net#3) shows further widget access from exactly that
        // call stack can throw MissingMethodException. Posting breaks out
        // of that stack first.
        _win.OnClose += (_, _) => { _uiQ.Post(DoLeave); };

        const float pad = 14f;
        float y = pad;

        // ── Map preview ──────────────────────────────────────────────────────
        var previewPanel = new TGUI.Panel();
        previewPanel.Position = new TGUI.Vector2f(pad, y);
        previewPanel.Size     = new TGUI.Vector2f(dw - pad * 2f, 160f);
        previewPanel.Renderer.SetProperty("BackgroundColor", Theme.Rgb(20, 22, 30));
        _win.Add(previewPanel);
        y += 160f + 10f;
        LoadPreview(previewPanel, game.MapName);

        // ── Title / map / mod ────────────────────────────────────────────────
        var titleLbl = new TGUI.Label(); titleLbl.Text = game.Title; titleLbl.TextSize = 16;
        titleLbl.Position = new TGUI.Vector2f(pad, y);
        titleLbl.Size     = new TGUI.Vector2f(dw - pad * 2f, 22f);
        titleLbl.Renderer.SetProperty("TextColor", Theme.Rgb(230, 232, 238));
        _win.Add(titleLbl);
        y += 24f;

        var mapLbl = new TGUI.Label(); mapLbl.Text = game.MapName; mapLbl.TextSize = 12;
        mapLbl.Position = new TGUI.Vector2f(pad, y);
        mapLbl.Size     = new TGUI.Vector2f(dw - pad * 2f, 18f);
        mapLbl.Renderer.SetProperty("TextColor", Theme.Rgb(160, 168, 188));
        _win.Add(mapLbl);
        y += 22f;

        // ── Status (STAGING / BATTLEROOM / etc) ──────────────────────────────
        _statusLabel = new TGUI.Label(); _statusLabel.TextSize = 13;
        _statusLabel.Position = new TGUI.Vector2f(pad, y);
        _statusLabel.Size     = new TGUI.Vector2f(dw - pad * 2f, 20f);
        _win.Add(_statusLabel);
        y += 24f;

        // ── Player count ─────────────────────────────────────────────────────
        _playerCountLabel = new TGUI.Label(); _playerCountLabel.TextSize = 12;
        _playerCountLabel.Position = new TGUI.Vector2f(pad, y);
        _playerCountLabel.Size     = new TGUI.Vector2f(dw - pad * 2f, 18f);
        _playerCountLabel.Renderer.SetProperty("TextColor", Theme.Rgb(160, 168, 188));
        _win.Add(_playerCountLabel);
        y += 26f;

        // ── Roster (team -> players) ──────────────────────────────────────────
        var rosterLbl = new TGUI.Label(); rosterLbl.Text = "Players"; rosterLbl.TextSize = 12;
        rosterLbl.Position = new TGUI.Vector2f(pad, y);
        rosterLbl.Renderer.SetProperty("TextColor", Theme.Rgb(140, 150, 175));
        _win.Add(rosterLbl);
        y += 20f;

        _rosterPanel = new TGUI.Panel();
        _rosterPanel.Position = new TGUI.Vector2f(pad, y);
        _rosterPanel.Size     = new TGUI.Vector2f(dw - pad * 2f, dh - y - pad - 40f);
        _rosterPanel.Renderer.SetProperty("BackgroundColor", Theme.Rgb(20, 22, 30));
        _win.Add(_rosterPanel);

        // ── Leave / Start buttons ─────────────────────────────────────────────
        var leaveBtn = new TGUI.Button(); leaveBtn.Text = "Leave"; leaveBtn.TextSize = 13;
        leaveBtn.Position = new TGUI.Vector2f(pad, dh - pad - 28f);
        leaveBtn.Size     = new TGUI.Vector2f((dw - pad * 2f) / 2f - 5f, 28f);
        Theme.ApplyDangerButton(leaveBtn.Renderer);
        leaveBtn.OnPress += (_, _) => DoLeave();
        _win.Add(leaveBtn);

        bool isHost = string.Equals(game.Host, _us.Username, StringComparison.OrdinalIgnoreCase);
        var startBtn = new TGUI.Button();
        startBtn.Text     = isHost ? "Start" : "Ready";
        startBtn.TextSize = 13;
        startBtn.Position = new TGUI.Vector2f(pad + (dw - pad * 2f) / 2f + 5f, dh - pad - 28f);
        startBtn.Size     = new TGUI.Vector2f((dw - pad * 2f) / 2f - 5f, 28f);
        Theme.ApplyPrimaryButton(startBtn.Renderer);
        startBtn.OnPress += (_, _) =>
        {
            // Sends "/launch" to gpgnet4ta's console port — the real
            // trigger that makes it proceed past staging and actually
            // start the game (ported from the real client's "/launch"
            // extended message over taflib::ConsoleReader). Before this,
            // gpgnet4ta auto-launched the instant HostGame/JoinGame
            // arrived, which meant this staging screen could never appear
            // before the game window did — that's now fixed: gpgnet4ta
            // waits, fully staged, until this signal arrives.
            Console.WriteLine("[STAGING] Start pressed — sending /launch to gpgnet4ta");
            startBtn.Enabled = false;
            startBtn.Text = isHost ? "Starting…" : "Ready";
            _ = Task.Run(async () =>
            {
                try { await _gls.StartGameAsync(); }
                catch (Exception ex) { Console.WriteLine($"[STAGING] StartGameAsync failed: {ex.Message}"); }
            });
        };
        _win.Add(startBtn);

        _gui.Add(_win, "GameStagingScreen");

        RefreshFromGame(game);

        _updatedSub = _gs.GameUpdated.Subscribe(g =>
        {
            if (_game is not null && g.Id == _game.Id)
                _uiQ.Post(() => RefreshFromGame(g));
        });
        _currentGameSub = _gs.CurrentGameChanged.Subscribe(g =>
        {
            if (g is null) _uiQ.Post(Close);
        });
    }

    private void RefreshFromGame(Game game)
    {
        _game = game;
        if (_win is not null) _win.Title = $"{game.Host}'s Game";

        if (_statusLabel is not null)
        {
            _statusLabel.Text = game.Status.ToString().ToUpperInvariant();
            _statusLabel.Renderer.SetProperty("TextColor",
                game.Status == GameStatus.Battleroom ? Theme.Rgb(100, 200, 110) : Theme.Rgb(220, 180, 90));
        }

        if (_playerCountLabel is not null)
            _playerCountLabel.Text = $"{game.NumPlayers}/{game.MaxPlayers} players";

        RebuildRoster(game);
    }

    private void RebuildRoster(Game game)
    {
        if (_rosterPanel is null) return;
        _rosterPanel.RemoveAllWidgets();

        float y = 6f;
        const float rowH = 22f;

        if (game.Teams.Count == 0)
        {
            var noneLbl = new TGUI.Label(); noneLbl.Text = "(waiting for players…)"; noneLbl.TextSize = 12;
            noneLbl.Position = new TGUI.Vector2f(8f, y);
            noneLbl.Renderer.SetProperty("TextColor", Theme.Rgb(120, 128, 148));
            _rosterPanel.Add(noneLbl);
            return;
        }

        foreach (var (teamKey, players) in game.Teams.OrderBy(kv => kv.Key))
        {
            var teamLbl = new TGUI.Label();
            teamLbl.Text     = teamKey == "-1" || teamKey == "0" ? "No Team" : $"Team {teamKey}";
            teamLbl.TextSize = 12;
            teamLbl.Position = new TGUI.Vector2f(8f, y);
            teamLbl.Renderer.SetProperty("TextColor", Theme.Rgb(170, 178, 198));
            _rosterPanel.Add(teamLbl);
            y += rowH;

            foreach (var player in players)
            {
                var pLbl = new TGUI.Label();
                pLbl.Text     = player;
                pLbl.TextSize = 12;
                pLbl.Position = new TGUI.Vector2f(22f, y);
                bool isHost = string.Equals(player, game.Host, StringComparison.OrdinalIgnoreCase);
                pLbl.Renderer.SetProperty("TextColor", isHost ? Theme.Rgb(230, 200, 100) : Theme.Rgb(200, 204, 214));
                _rosterPanel.Add(pLbl);
                y += rowH;
            }
        }
    }

    private void LoadPreview(TGUI.Panel previewPanel, string mapName)
    {
        _ = Task.Run(async () =>
        {
            byte[]? png = await _ms.GetThumbnailBytesAsync(mapName);
            if (png is null) return;

            string tmp = Path.Combine(Path.GetTempPath(), $"staging_preview_{Guid.NewGuid():N}.png");
            try
            {
                await File.WriteAllBytesAsync(tmp, png);
                _uiQ.Post(() =>
                {
                    try
                    {
                        var pic = new TGUI.Picture();
                        pic.Renderer.SetProperty("Texture", tmp);
                        pic.Position = new TGUI.Vector2f(0, 0);
                        pic.Size     = previewPanel.Size;
                        previewPanel.Add(pic);
                        _mapPreview = pic;
                    }
                    catch (Exception ex) { Console.WriteLine($"[STAGING] Preview render error: {ex.Message}"); }
                    finally { try { File.Delete(tmp); } catch { } }
                });
            }
            catch (Exception ex) { Console.WriteLine($"[STAGING] Preview load error: {ex.Message}"); }
        });
    }

    private void DoLeave()
    {
        _ = Task.Run(async () =>
        {
            try { await _gls.LeaveGameAsync(); }
            catch (Exception ex) { Console.WriteLine($"[STAGING] LeaveGameAsync failed: {ex.Message}"); }
        });
        Close();
    }

    private void Close()
    {
        _updatedSub?.Dispose();
        _currentGameSub?.Dispose();
        _mapPreview?.Dispose();
        _mapPreview = null;
        if (_win is not null) { _gui.Remove(_win); _win.Dispose(); }
        _win = null;
        if (ReferenceEquals(s_current, this)) s_current = null;
    }
}
