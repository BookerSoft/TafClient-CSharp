using TafClient.UI;

namespace TafClient.UI.Widgets;

/// <summary>
/// Custom file/directory browser built from TGUI primitives.
///
/// TGUI.FileDialog's C# binding does not expose Filesystem, AddFileTypeFilter,
/// or GetSelectedPaths in TGUI.Net 1.12. Instead we build a ChildWindow with:
///   - Path edit box (type or paste path directly)
///   - ListView of directory entries
///   - Up / Home / Navigate buttons
///   - Select button that returns the chosen path
/// </summary>
public static class ExePathDialog
{
    public static void Show(
        TGUI.Gui       gui,
        string         title,
        string?        initialPath,
        UiThreadQueue  uiQ,
        Action<string> onSelected,
        bool           folderMode = false)
    {
        const float dw = 560f, dh = 420f;

        // Starting directory
        string startDir = initialPath != null && System.IO.File.Exists(initialPath)
            ? System.IO.Path.GetDirectoryName(initialPath) ?? HomeDir()
            : initialPath != null && System.IO.Directory.Exists(initialPath)
                ? initialPath
                : HomeDir();

        // State
        string currentDir = startDir;

        // ── Window ────────────────────────────────────────────────────────────
        var win = new TGUI.ChildWindow();
        win.Title        = title;
        win.Size         = new TGUI.Vector2f(dw, dh);
        win.Position     = new TGUI.Vector2f(
            (gui.GetView().Size.X - dw) / 2f,
            (gui.GetView().Size.Y - dh) / 2f);
        win.Resizable    = false;
        Theme.ApplyChildWindow(win.Renderer);
        win.OnClose += (_, _) => gui.Remove(win);

        const float pad = 10f, bh = 28f;

        // ── Path bar ──────────────────────────────────────────────────────────
        var upBtn = new TGUI.Button(); upBtn.Text = "↑"; upBtn.TextSize = 13;
        upBtn.Position = new TGUI.Vector2f(pad, pad);
        upBtn.Size     = new TGUI.Vector2f(30f, bh);
        Theme.ApplySecondaryButton(upBtn.Renderer);
        win.Add(upBtn);

        var homeBtn = new TGUI.Button(); homeBtn.Text = "⌂"; homeBtn.TextSize = 13;
        homeBtn.Position = new TGUI.Vector2f(pad + 34f, pad);
        homeBtn.Size     = new TGUI.Vector2f(30f, bh);
        Theme.ApplySecondaryButton(homeBtn.Renderer);
        win.Add(homeBtn);

        var pathBox = new TGUI.EditBox();
        pathBox.Position = new TGUI.Vector2f(pad + 68f, pad);
        pathBox.Size     = new TGUI.Vector2f(dw - pad * 2f - 68f, bh);
        pathBox.TextSize = 12;
        pathBox.Text     = currentDir;
        Theme.ApplyEditBox(pathBox.Renderer);
        win.Add(pathBox, "PathBox");

        // ── File list ─────────────────────────────────────────────────────────
        float listY = pad + bh + 6f;
        float listH = dh - listY - bh - pad * 3f;

        var list = new TGUI.ListView();
        list.Position = new TGUI.Vector2f(pad, listY);
        list.Size     = new TGUI.Vector2f(dw - pad * 2f, listH);
        list.TextSize = 12;
        list.HeaderTextSize = 12;
        Theme.ApplyListView(list.Renderer);
        list.AddColumn("Name",     (uint)((dw - pad * 2f) * 0.60f));
        list.AddColumn("Type",     80);
        list.AddColumn("Size",     80);
        win.Add(list, "FileList");

        // ── Bottom bar ────────────────────────────────────────────────────────
        float by = listY + listH + pad;

        var selBox = new TGUI.EditBox();
        selBox.Position    = new TGUI.Vector2f(pad, by);
        selBox.Size        = new TGUI.Vector2f(dw - pad * 2f - (folderMode ? 170f : 170f), bh);
        selBox.DefaultText = "Selected path…";
        selBox.TextSize    = 12;
        selBox.Text        = folderMode ? startDir : (initialPath ?? "");
        Theme.ApplyEditBox(selBox.Renderer);
        win.Add(selBox, "SelBox");

        var selBtn = new TGUI.Button(); selBtn.Text = folderMode ? "Select Folder" : "Select"; selBtn.TextSize = 13;
        selBtn.Position = new TGUI.Vector2f(dw - pad - 160f, by);
        selBtn.Size     = new TGUI.Vector2f(75f, bh);
        Theme.ApplyPrimaryButton(selBtn.Renderer);
        win.Add(selBtn);

        var cancelBtn = new TGUI.Button(); cancelBtn.Text = "Cancel"; cancelBtn.TextSize = 13;
        cancelBtn.Position = new TGUI.Vector2f(dw - pad - 78f, by);
        cancelBtn.Size     = new TGUI.Vector2f(75f, bh);
        Theme.ApplySecondaryButton(cancelBtn.Renderer);
        win.Add(cancelBtn);

        // ── Directory populator ───────────────────────────────────────────────
        void Populate(string dir)
        {
            if (!System.IO.Directory.Exists(dir)) return;
            currentDir = dir;
            pathBox.Text = dir;
            if (folderMode) selBox.Text = dir;
            list.RemoveAllItems();
            try
            {
                // Directories first
                foreach (var d in System.IO.Directory.GetDirectories(dir)
                                       .OrderBy(x => x))
                {
                    string name = System.IO.Path.GetFileName(d);
                    list.AddItem(new[] { "[D] " + name, "Folder", "" });
                }
                // Then files — highlight executables
                foreach (var f in System.IO.Directory.GetFiles(dir)
                                       .OrderBy(x => x))
                {
                    string name = System.IO.Path.GetFileName(f);
                    string ext  = System.IO.Path.GetExtension(f).ToLowerInvariant();
                    bool   isExe = ext is ".exe" or "" && IsExecutable(f);
                    long   size;
                    try { size = new System.IO.FileInfo(f).Length; } catch { size = 0; }
                    string sizeStr = size < 1024 ? $"{size} B"
                        : size < 1024 * 1024      ? $"{size / 1024} KB"
                        : $"{size / 1024 / 1024} MB";
                    list.AddItem(new[] { (isExe ? "[X] " : "    ") + name, ext, sizeStr });
                }
            }
            catch (Exception ex)
            {
                list.AddItem(new[] { $"⚠ {ex.Message}", "", "" });
            }
        }

        // ── Events ────────────────────────────────────────────────────────────

        upBtn.OnPress += (_, _) =>
        {
            string? parent = System.IO.Path.GetDirectoryName(currentDir);
            if (parent != null) Populate(parent);
        };

        homeBtn.OnPress += (_, _) => Populate(HomeDir());

        pathBox.OnReturnKeyPress += (_, _) =>
        {
            string p = pathBox.Text.Trim();
            if (System.IO.Directory.Exists(p)) Populate(p);
            else if (System.IO.File.Exists(p)) selBox.Text = p;
        };

        list.OnItemSelect += (_, a) =>
        {
            if (a.Index < 0) return;
            string name = StripPrefix(list.GetItemCell(a.Index, 0));
            string full = System.IO.Path.Combine(currentDir, name);
            if (folderMode)
            {
                if (System.IO.Directory.Exists(full)) selBox.Text = full;
                // else: clicking a file in folder mode does nothing — selBox
                // stays on whatever folder was last valid.
            }
            else
            {
                selBox.Text = full;
            }
        };

        // Double-click on a list item: navigate into dirs, select files
        list.OnDoubleClick += (_, a) =>
        {
            if (a.Index < 0) return;
            string name = StripPrefix(list.GetItemCell(a.Index, 0));
            string full = System.IO.Path.Combine(currentDir, name);
            if (System.IO.Directory.Exists(full))
                Populate(full);
            else if (System.IO.File.Exists(full))
            {
                selBox.Text = full;
                uiQ.Post(() => onSelected(full));
                gui.Remove(win);
            }
        };

        selBtn.OnPress += (_, _) =>
        {
            string p = selBox.Text.Trim();
            if (p.Length > 0)
            {
                uiQ.Post(() => onSelected(p));
                gui.Remove(win);
            }
        };

        cancelBtn.OnPress += (_, _) => gui.Remove(win);

        // ── Initial populate ──────────────────────────────────────────────────
        Populate(currentDir);
        gui.Add(win, "ExePathDialog");
    }

    private static string HomeDir() =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Strips the [D], [X], or space prefix added in Populate.</summary>
    private static string StripPrefix(string cell)
    {
        if (cell.StartsWith("[D] ") || cell.StartsWith("[X] "))
            return cell[4..];
        return cell.TrimStart(' ');
    }

    private static bool IsExecutable(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".exe") return true;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                var fi = new System.IO.FileInfo(path);
                // Check execute bit via UnixFileMode on .NET 6+
                var mode = fi.UnixFileMode;
                return (mode & (UnixFileMode.UserExecute |
                                UnixFileMode.GroupExecute |
                                UnixFileMode.OtherExecute)) != 0;
            }
            catch { return false; }
        }
        return false;
    }
}
