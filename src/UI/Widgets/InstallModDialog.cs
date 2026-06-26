using TafClient.Service;
using TafClient.UI;

namespace TafClient.UI.Widgets;

/// <summary>
/// Install Mod dialog — macOS/Linux only. Lets the user pick which mod
/// they're installing and a source folder (their existing TA install,
/// wherever it lives on the real filesystem — e.g. a folder they already
/// have from Windows/Steam/a backup), then copies it into the Wine prefix's
/// drive_c (~/.wine-taf/drive_c/TAF/{mod} by default) so Wine sees a normal
/// C:\TAF\{mod}\TotalA.exe path when launching, exactly as it would on real
/// Windows.
///
/// On Windows itself this dialog isn't needed — there's no Wine prefix to
/// copy into, so HostGameDialog's existing "Install Mod" button (which just
/// lets the user point at wherever TA is already installed, no copying)
/// covers that case. This dialog is specifically for the macOS/Linux
/// "copy into the prefix" workflow.
/// </summary>
public static class InstallModDialog
{
    private static readonly (string Tech, string Display)[] KnownMods =
    [
        ("tacc",      "Core Contingency"),
        ("taesc",     "Escalation"),
        ("tazero",    "Total Annihilation Zero"),
        ("tamayhem",  "Mayhem"),
        ("tavmod",    "TA Vanilla Mod"),
        ("tatw",      "Total War"),
        ("coop",      "Co-op"),
        ("ladder1v1", "1v1 Ladder"),
    ];

    public static void Show(TGUI.Gui gui, UiThreadQueue uiQ, PreferencesService prefs, MapService ms)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            Console.WriteLine("[INSTALL] InstallModDialog is only meaningful on macOS/Linux (Wine prefix install) — ignoring on this platform");
            return;
        }

        const float dw = 520f, dh = 280f;
        var win = new TGUI.ChildWindow();
        win.Title    = "Install Mod";
        win.Size     = new TGUI.Vector2f(dw, dh);
        win.Position = new TGUI.Vector2f(
            (gui.GetView().Size.X - dw) / 2f,
            (gui.GetView().Size.Y - dh) / 2f);
        win.Resizable = false;
        Theme.ApplyChildWindow(win.Renderer);
        win.OnClose += (_, _) => gui.Remove(win);

        const float pad = 14f, rh = 28f, gap = 10f;
        float y = pad;

        // ── Mod selector ──────────────────────────────────────────────────────
        var modLbl = new TGUI.Label(); modLbl.Text = "Mod:"; modLbl.TextSize = 12;
        modLbl.Position = new TGUI.Vector2f(pad, y + 6f);
        modLbl.Size     = new TGUI.Vector2f(70f, rh);
        win.Add(modLbl);

        var modCombo = new TGUI.ComboBox();
        modCombo.Position = new TGUI.Vector2f(pad + 74f, y);
        modCombo.Size     = new TGUI.Vector2f(dw - pad * 2f - 74f, rh);
        modCombo.TextSize = 12;
        foreach (var (_, display) in KnownMods) modCombo.AddItem(display);
        modCombo.SetSelectedItemByIndex(4); // default to TA Vanilla Mod (tavmod) — the one used throughout this session
        win.Add(modCombo);
        y += rh + gap;

        // ── Source folder picker ─────────────────────────────────────────────
        var srcLbl = new TGUI.Label(); srcLbl.Text = "Source folder:"; srcLbl.TextSize = 12;
        srcLbl.Position = new TGUI.Vector2f(pad, y + 6f);
        srcLbl.Size     = new TGUI.Vector2f(110f, rh);
        win.Add(srcLbl);

        var srcBox = new TGUI.EditBox();
        srcBox.Position    = new TGUI.Vector2f(pad + 114f, y);
        srcBox.Size        = new TGUI.Vector2f(dw - pad * 2f - 114f - 38f, rh);
        srcBox.DefaultText = "Pick the folder containing TotalA.exe…";
        srcBox.TextSize    = 12;
        Theme.ApplyEditBox(srcBox.Renderer);
        win.Add(srcBox);

        var browseBtn = new TGUI.Button(); browseBtn.Text = "…"; browseBtn.TextSize = 13;
        browseBtn.Position = new TGUI.Vector2f(dw - pad - 34f, y);
        browseBtn.Size     = new TGUI.Vector2f(34f, rh);
        Theme.ApplySecondaryButton(browseBtn.Renderer);
        win.Add(browseBtn);
        y += rh + gap;

        // ── Destination preview (real path + Windows path) ──────────────────
        var destLbl = new TGUI.Label(); destLbl.TextSize = 11;
        destLbl.Position = new TGUI.Vector2f(pad, y);
        destLbl.Size     = new TGUI.Vector2f(dw - pad * 2f, 36f);
        destLbl.Renderer.SetProperty("TextColor", Theme.Rgb(150, 158, 178));
        win.Add(destLbl, "DestPreview");
        y += 40f + gap;

        // ── Progress bar (hidden until install starts) ───────────────────────
        var progressLbl = new TGUI.Label(); progressLbl.TextSize = 11;
        progressLbl.Position = new TGUI.Vector2f(pad, y);
        progressLbl.Size     = new TGUI.Vector2f(dw - pad * 2f, 18f);
        progressLbl.Visible  = false;
        win.Add(progressLbl, "ProgressLabel");
        y += 22f;

        var progressBar = new TGUI.ProgressBar();
        progressBar.Position = new TGUI.Vector2f(pad, y);
        progressBar.Size     = new TGUI.Vector2f(dw - pad * 2f, 18f);
        progressBar.Minimum  = 0;
        progressBar.Maximum  = 100;
        progressBar.Value    = 0;
        progressBar.Visible  = false;
        win.Add(progressBar, "ProgressBar");
        y += 22f + gap;

        // ── Buttons ───────────────────────────────────────────────────────────
        var installBtn = new TGUI.Button(); installBtn.Text = "Install"; installBtn.TextSize = 13;
        installBtn.Position = new TGUI.Vector2f(dw - pad - 160f, dh - pad - 28f);
        installBtn.Size     = new TGUI.Vector2f(75f, 28f);
        Theme.ApplyPrimaryButton(installBtn.Renderer);
        win.Add(installBtn);

        var cancelBtn = new TGUI.Button(); cancelBtn.Text = "Cancel"; cancelBtn.TextSize = 13;
        cancelBtn.Position = new TGUI.Vector2f(dw - pad - 78f, dh - pad - 28f);
        cancelBtn.Size     = new TGUI.Vector2f(75f, 28f);
        Theme.ApplySecondaryButton(cancelBtn.Renderer);
        win.Add(cancelBtn);

        // ── Logic ─────────────────────────────────────────────────────────────
        void UpdateDestPreview()
        {
            string tech = KnownMods[modCombo.SelectedItemIndex >= 0 ? modCombo.SelectedItemIndex : 4].Tech;
            string realDest = Path.Combine(WineDetector.GetWineDriveC(), "TAF", tech);
            string? winPath = WineDetector.ToWindowsPath(realDest);
            destLbl.Text = $"Will copy to:\n{realDest}\n(Wine sees this as {winPath ?? "C:\\TAF\\" + tech})";
        }
        UpdateDestPreview();
        modCombo.OnItemSelect += (_, _) => UpdateDestPreview();

        browseBtn.OnPress += (_, _) =>
            ExePathDialog.Show(
                gui,
                "Select the folder containing TotalA.exe",
                srcBox.Text.Length > 0 ? srcBox.Text : null,
                uiQ,
                path => srcBox.Text = path,
                folderMode: true);

        cancelBtn.OnPress += (_, _) => gui.Remove(win);

        installBtn.OnPress += (_, _) =>
        {
            string source = srcBox.Text.Trim();
            if (string.IsNullOrEmpty(source) || !Directory.Exists(source))
            {
                Console.WriteLine("[INSTALL] No valid source folder selected");
                destLbl.Text = "Pick a valid source folder first.";
                return;
            }

            int idx = modCombo.SelectedItemIndex >= 0 ? modCombo.SelectedItemIndex : 4;
            string tech = KnownMods[idx].Tech;
            string display = KnownMods[idx].Display;

            installBtn.Enabled = false;
            cancelBtn.Enabled  = false;
            progressLbl.Visible = true;
            progressBar.Visible = true;
            progressLbl.Text = "Starting copy…";
            progressBar.Value = 0;

            var progress = new Progress<(int done, int total)>(p =>
                uiQ.Post(() =>
                {
                    int pct = p.total > 0 ? (int)(100.0 * p.done / p.total) : 0;
                    progressBar.Value = pct;
                    progressLbl.Text = $"Copying… {p.done}/{p.total} files ({pct}%)";
                }));

            _ = Task.Run(async () =>
            {
                try
                {
                    string realDest = await ModInstallService.InstallAsync(source, tech, progress);
                    string? winPath = WineDetector.ToWindowsPath(realDest);

                    uiQ.Post(() =>
                    {
                        prefs.SetModExePath(tech, Path.Combine(realDest, "TotalA.exe"));
                        ms.RefreshMod(tech, force: true);
                        progressLbl.Text = $"Done. {display} installed at {realDest}" +
                            (winPath is not null ? $" (Wine: {winPath})" : "");
                        progressBar.Value = 100;
                        installBtn.Enabled = true;
                        cancelBtn.Enabled  = true;
                        cancelBtn.Text     = "Close";
                        Console.WriteLine($"[INSTALL] {tech} installed: real={realDest} wine={winPath}");
                    });
                }
                catch (Exception ex)
                {
                    uiQ.Post(() =>
                    {
                        progressLbl.Text = $"Failed: {ex.Message}";
                        installBtn.Enabled = true;
                        cancelBtn.Enabled  = true;
                        Console.WriteLine($"[INSTALL] Failed for {tech}: {ex}");
                    });
                }
            });
        };

        gui.Add(win, "InstallModDialog");
    }
}
