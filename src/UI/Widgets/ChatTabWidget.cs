using TafClient.Service;
using TafClient.UI;

namespace TafClient.UI.Widgets;

public sealed class ChatTabWidget
{
    private readonly PlayerService _ps;
    private readonly UiThreadQueue _uiQ;
    private TGUI.ChatBox? _chat;
    private TGUI.EditBox? _input;
    private TGUI.ListBox? _playerList;

    public ChatTabWidget(PlayerService ps, UiThreadQueue uiQ) { _ps = ps; _uiQ = uiQ; }

    public void Build(TGUI.Panel parent, float w, float h)
    {
        const float pad = 8f, sideW = 185f, inputH = 36f;
        float cx    = sideW + pad * 2f;
        float chatW = w - cx - pad;
        float chatH = h - inputH - pad * 3f - 22f;

        // ── Sidebar: online players ───────────────────────────────────────────
        var sideLbl = MkLabel("Online Players", 11, Theme.Rgb(120, 132, 158));
        sideLbl.Position = new TGUI.Vector2f(pad, pad);
        sideLbl.Size     = new TGUI.Vector2f(sideW, 18f);
        parent.Add(sideLbl);

        _playerList = new TGUI.ListBox();
        _playerList.Position = new TGUI.Vector2f(pad, pad + 20f);
        _playerList.Size     = new TGUI.Vector2f(sideW, h - pad * 2f - 20f);
        _playerList.TextSize = 12;
        Theme.ApplyListBox(_playerList.Renderer);
        parent.Add(_playerList, "PlayerList");

        // Populate with already-online players
        foreach (var p in _ps.Players.Values)
            _playerList.AddItem(p.Username);

        // New players arriving — post to UI thread
        _ps.PlayerOnline.Subscribe(p =>
            _uiQ.Post(() => _playerList?.AddItem(p.Username)));

        // ── Channel tabs strip (single channel for now) ───────────────────────
        var chanLbl = MkLabel("#taforever", 13, Theme.Rgb(85, 148, 235));
        chanLbl.Position = new TGUI.Vector2f(cx, pad);
        chanLbl.Size     = new TGUI.Vector2f(chatW, 18f);
        parent.Add(chanLbl);

        // ── Chat history ──────────────────────────────────────────────────────
        _chat = new TGUI.ChatBox();
        _chat.Position = new TGUI.Vector2f(cx, pad + 22f);
        _chat.Size     = new TGUI.Vector2f(chatW, chatH);
        _chat.TextSize = 13;
        Theme.ApplyChatBox(_chat.Renderer);
        _chat.AddLine("*** Welcome to TA Forever — #taforever ***", new TGUI.Color(80, 165, 80));
        _chat.AddLine("Type a message below and press Enter or click Send.",
            new TGUI.Color(95, 104, 128));
        parent.Add(_chat, "ChatBox");

        // ── Input bar ─────────────────────────────────────────────────────────
        float iy   = pad + 22f + chatH + pad;
        float btnW = 78f;
        _input = new TGUI.EditBox();
        _input.Position    = new TGUI.Vector2f(cx, iy);
        _input.Size        = new TGUI.Vector2f(chatW - btnW - 4f, inputH);
        _input.DefaultText = "Type a message and press Enter…";
        _input.TextSize    = 13;
        Theme.ApplyEditBox(_input.Renderer);
        _input.OnReturnKeyPress += (_, _) => Send();
        parent.Add(_input, "ChatInput");

        var send = new TGUI.Button();
        send.Text     = "Send";
        send.Position = new TGUI.Vector2f(cx + chatW - btnW, iy);
        send.Size     = new TGUI.Vector2f(btnW, inputH);
        send.TextSize = 13;
        Theme.ApplyPrimaryButton(send.Renderer);
        send.OnPress += (_, _) => Send();
        parent.Add(send, "SendBtn");
    }

    private void Send()
    {
        if (_input == null || _chat == null) return;
        string text = _input.Text.Trim();
        if (text.Length == 0) return;
        string user = _ps.CurrentPlayer?.Username ?? "You";
        _chat.AddLine($"<{user}>  {text}", new TGUI.Color(215, 220, 235));
        _input.Text = string.Empty;
        // TODO: wire to ChatService.SendMessageAsync("#taforever", text)
    }

    private static TGUI.Label MkLabel(string t, uint sz, string color)
    {
        var l = new TGUI.Label(); l.Text = t; l.TextSize = (int)sz;
        l.Renderer.SetProperty("TextColor", color);
        return l;
    }
}
