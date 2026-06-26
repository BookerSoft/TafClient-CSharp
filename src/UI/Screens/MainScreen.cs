using TafClient.Service;
using TafClient.UI.Widgets;

namespace TafClient.UI.Screens;

public sealed class MainScreen
{
    private readonly TGUI.Gui               _gui;
    private readonly GameService            _gs;
    private readonly PlayerService          _ps;
    private readonly UserService            _us;
    private readonly MapService             _ms;
    private readonly UiThreadQueue          _uiQ;
    private readonly PreferencesService     _prefs;
    private readonly GameLaunchService      _gls;
    private MapSearchWidget?                _mapSearchWidget;

    public MainScreen(TGUI.Gui gui, GameService gs, PlayerService ps,
                      UserService us, MapService ms, UiThreadQueue uiQ,
                      PreferencesService prefs, GameLaunchService gls)
    { _gui = gui; _gs = gs; _ps = ps; _us = us; _ms = ms; _uiQ = uiQ; _prefs = prefs; _gls = gls; }

    public void Build(uint winW, uint winH)
    {
        const float statusH = 30f, tabH = 34f;

        // ── Status bar ────────────────────────────────────────────────────────
        var bar = new TGUI.Panel();
        bar.Position = new TGUI.Vector2f(0f, 0f);
        bar.Size     = new TGUI.Vector2f(winW, statusH);
        bar.Renderer.SetProperty("BackgroundColor", Theme.Rgb(16, 18, 24));

        var logoLbl = new TGUI.Label();
        logoLbl.Text = "TA Forever Client";  logoLbl.TextSize = 13;
        logoLbl.Position = new TGUI.Vector2f(10f, 7f);
        logoLbl.Renderer.SetProperty("TextColor", Theme.Rgb(125, 165, 225));
        bar.Add(logoLbl);

        var connLbl = new TGUI.Label();
        connLbl.Text = "●  Connected";  connLbl.TextSize = 11;
        connLbl.Position = new TGUI.Vector2f(winW / 2f - 50f, 8f);
        connLbl.Renderer.SetProperty("TextColor", Theme.Rgb(80, 200, 100));
        bar.Add(connLbl, "ConnLabel");

        // Update on connection state changes
        _gs.GetType(); // ensure gs is captured
        _us.GetType();

        // ── Logout button — shows "✓ username" when logged in, logs out on click ─
        var logoutBtn = new TGUI.Button();
        logoutBtn.Text     = BuildUserText(_us.Username);
        logoutBtn.TextSize = 12;
        logoutBtn.Position = new TGUI.Vector2f(winW - 220f, 2f);
        logoutBtn.Size     = new TGUI.Vector2f(210f, 26f);
        // Style as a subtle status button — green text, transparent background
        logoutBtn.Renderer.SetProperty("BackgroundColor",      "rgba(0,0,0,0)");
        logoutBtn.Renderer.SetProperty("BackgroundColorHover", Theme.Rgb(55, 30, 30));
        logoutBtn.Renderer.SetProperty("BackgroundColorDown",  Theme.Rgb(80, 20, 20));
        logoutBtn.Renderer.SetProperty("TextColor",            Theme.Rgb(110, 200, 120));
        logoutBtn.Renderer.SetProperty("TextColorHover",       Theme.Rgb(220, 120, 120));
        logoutBtn.Renderer.SetProperty("BorderColor",          "rgba(0,0,0,0)");
        logoutBtn.Renderer.SetProperty("BorderColorHover",     Theme.Rgb(120, 60, 60));
        logoutBtn.Renderer.SetProperty("Borders",              "1");
        logoutBtn.Renderer.SetProperty("RoundedBorderRadius",  "3");
        bar.Add(logoutBtn, "LogoutBtn");

        // Update text when username changes
        _us.UsernameChanged.Subscribe(name =>
            _uiQ.Post(() => logoutBtn.Text = BuildUserText(name)));

        // Click → logout + dispose all connections + return to login screen
        logoutBtn.OnPress += (_, _) =>
        {
            logoutBtn.Enabled = false;
            logoutBtn.Text    = "Logging out…";
            _mapSearchWidget?.Dispose();
            Task.Run(() =>
            {
                try { _us.LogOut(); } catch { }
                _uiQ.Post(() =>
                {
                    _gui.RemoveAllWidgets();
                    new LoginScreen(_gui, _us, () =>
                    {
                        _gui.RemoveAllWidgets();
                        new MainScreen(_gui, _gs, _ps, _us, _ms, _uiQ, _prefs, _gls).Build(winW, winH);
                    }, _uiQ).Build(winW, winH);
                });
            });
        };

        _gui.Add(bar, "StatusBar");

        // ── TabContainer ──────────────────────────────────────────────────────
        var tabs = new TGUI.TabContainer();
        tabs.Position = new TGUI.Vector2f(0f, statusH);
        tabs.Size     = new TGUI.Vector2f(winW, winH - statusH);
        tabs.SetTabsHeight(tabH);

        // Apply tab bar theme BEFORE adding to GUI and BEFORE AddTab calls
        // so all renderer properties are set on the shared renderer data first.
        Theme.ApplyTabContainer(tabs.TabsRenderer);

        float pw = winW, ph = winH - statusH - tabH;

        var playP     = tabs.AddTab("  Play  ");
        var chatP     = tabs.AddTab("  Chat  ");
        var mapsP     = tabs.AddTab("  Maps  ");
        var replaysP  = tabs.AddTab("  Replays  ");
        var lbP       = tabs.AddTab("  Ladder  ");
        var settingsP = tabs.AddTab("  Settings  ");

        // Style each content panel individually after creation
        foreach (var p in new[] { playP, chatP, mapsP, replaysP, lbP, settingsP })
        {
            p.Renderer.SetProperty("BackgroundColor", Theme.Rgb(20, 22, 28));
            p.Renderer.SetProperty("Borders",         "0");
            p.Renderer.SetProperty("Padding",         "0");
        }

        _gui.Add(tabs, "MainTabs");

        // Build each tab
        new PlayTabWidget(_gs, _ps, _gui, _uiQ, _ms, _prefs, _us, _gls).Build(playP,    pw, ph);
        new ChatTabWidget(_ps, _uiQ).Build(chatP,               pw, ph);
        _mapSearchWidget = new MapSearchWidget(_ms, _uiQ);
        _mapSearchWidget.Build(mapsP,             pw, ph);
        new ReplaysTabWidget().Build(replaysP,                   pw, ph);
        new LeaderboardTabWidget(_ps).Build(lbP,                 pw, ph);
        new SettingsTabWidget(_us, _gui, _uiQ, _prefs, _ms).Build(settingsP,  pw, ph);

        tabs.Select(0);
    }

    private static string BuildUserText(string? name) =>
        name is not null ? $"✓  {name}  [logout]" : "Not logged in";
}
