using Microsoft.Extensions.Logging;
using SFML.Graphics;
using SFML.Window;
using TafClient.Service;
using TafClient.UI.Screens;

namespace TafClient.UI;

public sealed class TafApp : IDisposable
{
    // Design-reference size: every screen's layout math is written against
    // this baseline. At startup we scale up/down from here based on the
    // actual desktop resolution, then keep the GUI in sync on every resize.
    private const uint   BaseW  = 1280;
    private const uint   BaseH  = 800;
    private const uint   MinW   = 1024;
    private const uint   MinH   = 640;
    private const string Title = "Downlord's TAF Client";

    private readonly ILogger<TafApp>   _log;
    private readonly UserService       _us;
    private readonly GameService       _gs;
    private readonly PlayerService     _ps;
    private readonly MapService        _ms;
    private readonly WinePrefixSetup   _wineSetup;
    private readonly UiThreadQueue     _uiQ;
    private readonly IDisposable       _resources;
    private readonly PreferencesService _prefs;
    private readonly GameLaunchService _gls;

    private RenderWindow? _window;
    private TGUI.Gui?     _gui;

    // Current logical window size — what every screen's Build(w, h) is called
    // with. Updated on every resize so screens always lay out against the
    // window's actual current size.
    private uint _curW = BaseW;
    private uint _curH = BaseH;

    // Tracks which top-level screen is showing, so a resize can rebuild the
    // current screen at the new size instead of just stretching it.
    private Action? _rebuildCurrentScreen;

    public TafApp(ILogger<TafApp> log, UserService us, GameService gs,
                  PlayerService ps, MapService ms, WinePrefixSetup wineSetup,
                  UiThreadQueue uiQ, IDisposable resources, PreferencesService prefs,
                  GameLaunchService gls)
    {
        _log       = log;
        _us        = us;
        _gs        = gs;
        _ps        = ps;
        _ms        = ms;
        _wineSetup = wineSetup;
        _uiQ       = uiQ;
        _resources = resources;
        _prefs     = prefs;
        _gls       = gls;
    }

    /// <summary>
    /// Picks an initial window size based on the desktop's actual resolution:
    /// scales the 1280x800 reference design up on larger displays (capped to
    /// avoid an absurdly large window on e.g. a 4K monitor) and down on
    /// smaller ones (never below MinW x MinH, where the UI stops being usable).
    /// Preserves the original 1280:800 (8:5) aspect ratio.
    /// </summary>
    private static (uint w, uint h) ComputeInitialSize()
    {
        var desktop = VideoMode.DesktopMode;
        uint deskW = desktop.Size.X;
        uint deskH = desktop.Size.Y;

        if (deskW == 0 || deskH == 0)
            return (BaseW, BaseH); // couldn't detect — fall back to the reference size

        // Target ~75% of desktop real estate, scaled from the reference size,
        // so the window comfortably fits alongside other windows rather than
        // filling the whole screen.
        const double targetFraction = 0.75;
        double scaleByWidth  = (deskW * targetFraction) / BaseW;
        double scaleByHeight = (deskH * targetFraction) / BaseH;
        double scale = Math.Min(scaleByWidth, scaleByHeight);

        uint w = (uint)Math.Clamp(BaseW * scale, MinW, deskW);
        uint h = (uint)Math.Clamp(BaseH * scale, MinH, deskH);

        return (w, h);
    }

    public void Run()
    {
        (_curW, _curH) = ComputeInitialSize();
        _log.LogInformation("Desktop resolution {DW}x{DH} — initial window size {W}x{H}",
            VideoMode.DesktopMode.Size.X, VideoMode.DesktopMode.Size.Y, _curW, _curH);

        _window = new RenderWindow(new VideoMode(new SFML.System.Vector2u(_curW, _curH)), Title);
        _window.SetFramerateLimit(60);
        _window.Closed   += (_, _) => OnWindowClose();
        _window.Resized  += OnWindowResized;

        _gui = new TGUI.Gui(_window);
        ShowSplash();

        while (_window.IsOpen)
        {
            _window.DispatchEvents();
            _uiQ.Drain();
            _window.Clear(new Color(14, 16, 22));
            _gui.Draw();
            _window.Display();
        }
    }

    /// <summary>
    /// Handles live window resizing: updates the SFML view so rendering isn't
    /// stretched/distorted, tells TGUI about the new size, and rebuilds the
    /// currently-visible screen against the new dimensions so its layout
    /// (which is computed from absolute w/h at Build() time) reflows correctly
    /// instead of just being cropped or leaving dead space.
    /// </summary>
    private void OnWindowResized(object? sender, SFML.Window.SizeEventArgs e)
    {
        if (e.Size.X < MinW || e.Size.Y < MinH)
        {
            // Enforce a minimum usable size — re-apply it if the user drags
            // the window smaller than the UI can reasonably lay out.
            uint w = Math.Max(e.Size.X, MinW);
            uint h = Math.Max(e.Size.Y, MinH);
            _window!.Size = new SFML.System.Vector2u(w, h);
            return; // the Size assignment above will raise Resized again with the clamped size
        }

        _curW = e.Size.X;
        _curH = e.Size.Y;

        // Keep the SFML view's logical size matching the new pixel size 1:1,
        // so widget coordinates (computed in pixels by every Build() method)
        // continue to line up with the actual window — without this the
        // content would stay rendered at the old size and just be
        // stretched/cropped by the new window bounds.
        var view = new SFML.Graphics.View(new SFML.Graphics.FloatRect(
            new SFML.System.Vector2f(0, 0),
            new SFML.System.Vector2f(_curW, _curH)));
        _window!.SetView(view);

        // NOTE: TGUI.Net's Gui is bound to this RenderWindow and is documented
        // to track the window's current size for hit-testing/layout purposes
        // on its own — it does not need (and in this version may not expose)
        // a separate manual view-sync call from here.

        // Rebuild the current screen at the new size. Debounced implicitly by
        // SFML only firing Resized on actual size changes (not every frame),
        // but dragging a window edge fires many of these in quick succession —
        // acceptable here since Build() calls are cheap widget construction,
        // not expensive I/O.
        _rebuildCurrentScreen?.Invoke();
    }

    private void OnWindowClose()
    {
        _log.LogInformation("Window closing — logging out");
        // LogOut calls Disconnect which closes the socket.
        // DisposableAggregate is disposed by the DI host on StopAsync —
        // do NOT call _resources.Dispose() here to avoid double-dispose.
        try { _us.LogOut(); } catch (Exception ex) { _log.LogWarning(ex, "Logout error"); }
        _window!.Close();
    }

    private void ShowSplash()
    {
        _gui!.RemoveAllWidgets();
        _rebuildCurrentScreen = ShowSplash;

        // Run Wine prefix setup in background while splash plays.
        // If setup is already done (sentinel file exists) this returns immediately.
        // Progress updates are posted back to the UI thread via _uiQ.
        var splash = new SplashScreen(_gui, () => _uiQ.Post(ShowLogin), _uiQ);
        splash.Build(_curW, _curH);

        if (_wineSetup.IsNeeded)
        {
            _log.LogInformation("Wine prefix setup required — running in background");
            var setupProgress = new Progress<(int pct, string status)>(p =>
                _uiQ.Post(() =>
                {
                    // Access splash widgets through the stored references on SplashScreen
                    splash.UpdateProgress(p.pct, p.status);
                }));

            _ = Task.Run(async () =>
            {
                bool ok = await _wineSetup.SetupAsync(setupProgress);
                if (!ok)
                    _log.LogWarning("Wine prefix setup failed — multiplayer may not work");
            });
        }
    }

    internal void ShowLogin()
    {
        _gui!.RemoveAllWidgets();
        _rebuildCurrentScreen = ShowLogin;
        new LoginScreen(_gui, _us, OnLoginSuccess, _uiQ).Build(_curW, _curH);
    }

    private void OnLoginSuccess()
    {
        _log.LogInformation("Login OK — starting background map scan");
        // Mirrors MapService.afterPropertiesSet() — scan all mods in background
        _ms.ScanAllModsInBackground();
        _gui!.RemoveAllWidgets();
        _rebuildCurrentScreen = () =>
        {
            _gui!.RemoveAllWidgets();
            new MainScreen(_gui, _gs, _ps, _us, _ms, _uiQ, _prefs, _gls).Build(_curW, _curH);
        };
        new MainScreen(_gui, _gs, _ps, _us, _ms, _uiQ, _prefs, _gls).Build(_curW, _curH);
    }

    public void Dispose()
    {
        _gui?.Dispose();
        _window?.Dispose();
    }
}
