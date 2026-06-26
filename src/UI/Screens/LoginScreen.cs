using TafClient.Service;

namespace TafClient.UI.Screens;

/// <summary>
/// Login screen. All TGUI mutations happen on the render thread via UiThreadQueue.
/// Background Task.Run calls LoginAsync; result is posted back to UI thread.
/// </summary>
public sealed class LoginScreen
{
    private readonly TGUI.Gui      _gui;
    private readonly UserService   _us;
    private readonly Action        _onSuccess;
    private readonly UiThreadQueue _uiQ;

    private TGUI.EditBox?  _user, _pass;
    private TGUI.CheckBox? _remember;
    private TGUI.Button?   _loginBtn;
    private TGUI.Label?    _errLbl;
    private TGUI.Panel?    _serverPanel;

    public LoginScreen(TGUI.Gui gui, UserService us, Action onSuccess, UiThreadQueue uiQ)
    { _gui = gui; _us = us; _onSuccess = onSuccess; _uiQ = uiQ; }

    public void Build(uint winW, uint winH)
    {
        const float pw = 440f, ph = 370f;
        float px = (winW - pw) / 2f, py = (winH - ph) / 2f;

        var card = new TGUI.Panel();
        card.Position = new TGUI.Vector2f(px, py);
        card.Size     = new TGUI.Vector2f(pw, ph);
        card.Renderer.SetProperty("BackgroundColor", Theme.Rgb(34, 38, 50));
        card.Renderer.SetProperty("BorderColor",     Theme.Rgb(60, 100, 175));
        card.Renderer.SetProperty("Borders",         "1");
        _gui.Add(card, "LoginCard");

        float x = 32f, y = 22f, w = pw - 64f, fh = 34f;

        // Title
        var title = MakeLabel("TA Forever — Login", 21, Theme.Rgb(195, 218, 255));
        title.Position = new TGUI.Vector2f(x, y);
        title.Size     = new TGUI.Vector2f(w, 30f);
        title.HorizontalAlignment = TGUI.HorizontalAlignment.Center;
        title.OnDoubleClick += (_, _) =>
        { if (_serverPanel != null) _serverPanel.Visible = !_serverPanel.Visible; };
        card.Add(title); y += 42f;

        // Username
        card.Add(FieldLabel("Username", x, y, w)); y += 21f;
        _user = MakeEditBox("Username", false); Place(_user, x, y, w, fh);
        _user.OnReturnKeyPress += (_, _) => DoLogin();
        card.Add(_user); y += fh + 10f;

        // Password
        card.Add(FieldLabel("Password", x, y, w)); y += 21f;
        _pass = MakeEditBox("Password", true); Place(_pass, x, y, w, fh);
        _pass.OnReturnKeyPress += (_, _) => DoLogin();
        card.Add(_pass); y += fh + 10f;

        // Remember me
        _remember = new TGUI.CheckBox();
        _remember.Text = "Remember me"; _remember.TextSize = 13;
        _remember.Position = new TGUI.Vector2f(x, y);
        _remember.Size     = new TGUI.Vector2f(20f, 20f);
        Theme.ApplyCheckBox(_remember.Renderer);
        card.Add(_remember); y += 30f;

        // Login button (full width)
        _loginBtn = new TGUI.Button();
        _loginBtn.Text     = "Login";
        _loginBtn.TextSize = 15;
        Place(_loginBtn, x, y, w, fh + 2f);
        Theme.ApplyPrimaryButton(_loginBtn.Renderer);
        _loginBtn.OnPress += (_, _) => DoLogin();
        card.Add(_loginBtn); y += fh + 16f;

        // Link row
        var cLink = MakeLabel("Create account", 12, Theme.Rgb(75, 140, 230));
        cLink.Position = new TGUI.Vector2f(x, y);
        card.Add(cLink);
        var fLink = MakeLabel("Forgot password?", 12, Theme.Rgb(75, 140, 230));
        fLink.Position = new TGUI.Vector2f(pw - x - 115f, y);
        card.Add(fLink); y += 24f;

        // Error label
        _errLbl = MakeLabel("", 12, Theme.Rgb(235, 70, 70));
        _errLbl.Position  = new TGUI.Vector2f(x, y);
        _errLbl.Size      = new TGUI.Vector2f(w, 22f);
        _errLbl.HorizontalAlignment = TGUI.HorizontalAlignment.Center;
        _errLbl.Visible   = false;
        card.Add(_errLbl);

        // Server config (hidden; double-click title to reveal)
        _serverPanel = BuildServerPanel(pw, ph);
        card.Add(_serverPanel);
    }

    private TGUI.Panel BuildServerPanel(float pw, float ph)
    {
        var p = new TGUI.Panel();
        p.Position = new TGUI.Vector2f(0f, ph + 4f);
        p.Size     = new TGUI.Vector2f(pw, 92f);
        p.Renderer.SetProperty("BackgroundColor", Theme.Rgb(28, 31, 42));
        p.Renderer.SetProperty("BorderColor",     Theme.Rgb(75, 80, 100));
        p.Renderer.SetProperty("Borders",         "1");
        p.Visible = false;

        float x = 12f, y = 10f, lw = 88f, fh = 26f;
        p.Add(FieldLabel("Host:", x, y + 4f, lw));
        var hBox = MakeEditBox("lobby.taforever.com", false);
        hBox.Text = "lobby.taforever.com";
        Place(hBox, x + lw + 4f, y, 190f, fh); p.Add(hBox); y += fh + 8f;

        p.Add(FieldLabel("Port:", x, y + 4f, lw));
        var pBox = MakeEditBox("8001", false);
        pBox.Text = "8001";
        Place(pBox, x + lw + 4f, y, 68f, fh); p.Add(pBox);
        return p;
    }

    private void DoLogin()
    {
        string username = _user?.Text.Trim() ?? "";
        string password = _pass?.Text ?? "";

        if (username.Length == 0 || password.Length == 0)
        { SetError("Enter username and password."); return; }
        if (username.Contains('@'))
        { SetError("Use your username, not email."); return; }

        if (_loginBtn != null) _loginBtn.Enabled = false;
        SetError(""); _errLbl!.Visible = false;

        bool autoLogin = _remember?.Checked ?? false;
        Task.Run(async () =>
        {
            try
            {
                await _us.LoginAsync(username, password, autoLogin);
                _uiQ.Post(_onSuccess);
            }
            catch (Exception ex)
            {
                string msg = ex.InnerException?.Message ?? ex.Message;
                _uiQ.Post(() =>
                {
                    SetError(msg);
                    if (_loginBtn != null) _loginBtn.Enabled = true;
                });
            }
        });
    }

    private void SetError(string msg)
    {
        if (_errLbl == null) return;
        _errLbl.Text    = msg;
        _errLbl.Visible = msg.Length > 0;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static TGUI.Label MakeLabel(string text, uint size, string colorRgb)
    {
        var l = new TGUI.Label();
        l.Text     = text;
        l.TextSize = (int)size;
        l.Renderer.SetProperty("TextColor", colorRgb);
        return l;
    }

    private static TGUI.Label FieldLabel(string text, float x, float y, float w)
    {
        var l = MakeLabel(text, 12, Theme.Rgb(148, 156, 175));
        l.Position = new TGUI.Vector2f(x, y);
        l.Size     = new TGUI.Vector2f(w, 18f);
        return l;
    }

    private static TGUI.EditBox MakeEditBox(string placeholder, bool password)
    {
        var e = new TGUI.EditBox();
        e.DefaultText = placeholder;
        e.TextSize    = 14;
        if (password) e.PasswordCharacter = "*";
        Theme.ApplyEditBox(e.Renderer);
        return e;
    }

    private static void Place(TGUI.Widget w, float x, float y, float width, float height)
    {
        w.Position = new TGUI.Vector2f(x, y);
        w.Size     = new TGUI.Vector2f(width, height);
    }
}
