using SFML.Graphics;

namespace TafClient.UI.Screens;

public sealed class SplashScreen
{
    private readonly TGUI.Gui      _gui;
    private readonly Action        _onReady;
    private readonly UiThreadQueue _uiQ;

    // Stored so TafApp can update them during Wine prefix setup
    private TGUI.ProgressBar? _progressBar;
    private TGUI.Label?       _statusLabel;

    public SplashScreen(TGUI.Gui gui, Action onReady, UiThreadQueue uiQ)
    { _gui = gui; _onReady = onReady; _uiQ = uiQ; }

    /// <summary>Called from TafApp to reflect Wine setup progress on the splash screen.</summary>
    public void UpdateProgress(int pct, string status)
    {
        if (_progressBar != null) _progressBar.Value = pct;
        if (_statusLabel  != null) _statusLabel.Text  = status;
    }

    public void Build(uint winW, uint winH)
    {
        var bg = new TGUI.Panel();
        bg.Position = new TGUI.Vector2f(0f, 0f);
        bg.Size     = new TGUI.Vector2f(winW, winH);
        bg.Renderer.SetProperty("BackgroundColor", Theme.Rgb(14, 16, 22));
        bg.Renderer.SetProperty("Borders",         "0");
        _gui.Add(bg, "SplashBg");

        float cx = winW / 2f;
        float cy = winH / 2f;

        // ── TA Forever logo (text-based, matches original TAF aesthetic) ───────
        // Large stylised title
        var title = new TGUI.Label();
        title.Text     = "TA Forever";
        title.TextSize = 64;
        title.Position = new TGUI.Vector2f(cx - 260f, cy - 120f);
        title.Size     = new TGUI.Vector2f(520f, 80f);
        title.HorizontalAlignment = TGUI.HorizontalAlignment.Center;
        title.Renderer.SetProperty("TextColor", Theme.Rgb(200, 215, 255));
        title.Renderer.SetProperty("TextStyle", "Bold");
        bg.Add(title);

        // Sub-tagline
        var sub = new TGUI.Label();
        sub.Text     = "TOTAL ANNIHILATION CLIENT";
        sub.TextSize = 18;
        sub.Position = new TGUI.Vector2f(cx - 200f, cy - 30f);
        sub.Size     = new TGUI.Vector2f(400f, 28f);
        sub.HorizontalAlignment = TGUI.HorizontalAlignment.Center;
        sub.Renderer.SetProperty("TextColor", Theme.Rgb(100, 140, 210));
        sub.Renderer.SetProperty("TextStyle", "Bold");
        bg.Add(sub);

        // Decorative separator line
        var sep = new TGUI.SeparatorLine();
        sep.Position = new TGUI.Vector2f(cx - 180f, cy + 10f);
        sep.Size     = new TGUI.Vector2f(360f, 2f);
        sep.Renderer.SetProperty("Color", Theme.Rgb(55, 90, 160));
        bg.Add(sep);

        // Version label
        var ver = new TGUI.Label();
        ver.Text     = "v2026.1  ·  C# Port";
        ver.TextSize = 13;
        ver.Position = new TGUI.Vector2f(cx - 100f, cy + 22f);
        ver.Size     = new TGUI.Vector2f(200f, 20f);
        ver.HorizontalAlignment = TGUI.HorizontalAlignment.Center;
        ver.Renderer.SetProperty("TextColor", Theme.Rgb(80, 90, 115));
        bg.Add(ver);

        // Progress bar
        _progressBar = new TGUI.ProgressBar();
        _progressBar.Position = new TGUI.Vector2f(cx - 180f, cy + 58f);
        _progressBar.Size     = new TGUI.Vector2f(360f, 6f);
        _progressBar.Minimum  = 0; _progressBar.Maximum = 100; _progressBar.Value = 0;
        _progressBar.Renderer.SetProperty("BackgroundColor", Theme.Rgb(28, 32, 42));
        _progressBar.Renderer.SetProperty("FillColor",       Theme.Rgb(55, 95, 200));
        _progressBar.Renderer.SetProperty("BorderColor",     Theme.Rgb(40, 50, 72));
        _progressBar.Renderer.SetProperty("Borders",         "1");
        bg.Add(_progressBar, "SplashProgress");

        // Status text
        _statusLabel = new TGUI.Label();
        _statusLabel.Text     = "Initialising…";
        _statusLabel.TextSize = 11;
        _statusLabel.Position = new TGUI.Vector2f(cx - 180f, cy + 70f);
        _statusLabel.Size     = new TGUI.Vector2f(360f, 18f);
        _statusLabel.HorizontalAlignment = TGUI.HorizontalAlignment.Center;
        _statusLabel.Renderer.SetProperty("TextColor", Theme.Rgb(75, 85, 110));
        bg.Add(_statusLabel, "SplashStatus");

        // ── Animate progress then hand off to login ───────────────────────────
        Task.Run(async () =>
        {
            var steps = new (string text, int pct)[]
            {
                ("Loading configuration…", 20),
                ("Connecting services…",   50),
                ("Preparing interface…",   80),
                ("Ready.",                100),
            };
            foreach (var (text, pct) in steps)
            {
                await Task.Delay(350);
                var t = text; var p = pct;
                _uiQ.Post(() => { try { this._statusLabel!.Text = t; this._progressBar!.Value = p; } catch { } });
            }
            await Task.Delay(250);
            _uiQ.Post(_onReady);
        });
    }
}
