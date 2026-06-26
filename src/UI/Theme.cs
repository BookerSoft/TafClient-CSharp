namespace TafClient.UI;

/// <summary>
/// Centralises all TGUI renderer property calls via the safe
/// <c>renderer.SetProperty(name, value)</c> string API.
///
/// This is the same mechanism used by TGUI theme files — every property
/// (BackgroundColor, TextColor, BorderColor, BackgroundColorHover, etc.)
/// is set as a string, so we are never blocked by missing strongly-typed
/// C# property names on specific renderer classes.
///
/// Color format: "rgb(r, g, b)" or "rgba(r, g, b, a)" — exactly as TGUI
/// theme files use, passed directly to the C++ layer.
/// </summary>
public static class Theme
{
    // ── Color helpers ─────────────────────────────────────────────────────────

    public static string Rgb(int r, int g, int b) => $"rgb({r}, {g}, {b})";

    // ── Widget style presets ──────────────────────────────────────────────────

    public static void ApplyDarkPanel(TGUI.WidgetRenderer r,
        int bgR = 38, int bgG = 41, int bgB = 50,
        int borderR = 55, int borderG = 58, int borderB = 70)
    {
        r.SetProperty("BackgroundColor", Rgb(bgR, bgG, bgB));
        r.SetProperty("BorderColor",     Rgb(borderR, borderG, borderB));
        r.SetProperty("Borders",         "1");
    }

    public static void ApplyPrimaryButton(TGUI.WidgetRenderer r)
    {
        r.SetProperty("BackgroundColor",         Rgb(55, 95, 155));
        r.SetProperty("BackgroundColorHover",    Rgb(72, 114, 180));
        r.SetProperty("BackgroundColorDown",     Rgb(44, 78, 130));
        r.SetProperty("BackgroundColorDisabled", Rgb(45, 50, 60));
        r.SetProperty("TextColor",               Rgb(225, 230, 255));
        r.SetProperty("TextColorHover",          Rgb(235, 240, 255));
        r.SetProperty("TextColorDown",           Rgb(210, 220, 245));
        r.SetProperty("TextColorDisabled",       Rgb(100, 105, 115));
        r.SetProperty("BorderColor",             Rgb(65, 105, 165));
        r.SetProperty("BorderColorHover",        Rgb(80, 120, 185));
        r.SetProperty("BorderColorDown",         Rgb(50, 88, 140));
        r.SetProperty("Borders",                 "1");
        r.SetProperty("RoundedBorderRadius",     "3");
    }

    public static void ApplySecondaryButton(TGUI.WidgetRenderer r)
    {
        r.SetProperty("BackgroundColor",      Rgb(48, 52, 63));
        r.SetProperty("BackgroundColorHover", Rgb(62, 67, 80));
        r.SetProperty("BackgroundColorDown",  Rgb(38, 42, 52));
        r.SetProperty("TextColor",            Rgb(190, 195, 210));
        r.SetProperty("TextColorHover",       Rgb(210, 215, 228));
        r.SetProperty("BorderColor",          Rgb(72, 78, 96));
        r.SetProperty("BorderColorHover",     Rgb(88, 95, 115));
        r.SetProperty("Borders",              "1");
        r.SetProperty("RoundedBorderRadius",  "3");
    }

    public static void ApplyDangerButton(TGUI.WidgetRenderer r)
    {
        r.SetProperty("BackgroundColor",      Rgb(140, 40, 40));
        r.SetProperty("BackgroundColorHover", Rgb(170, 55, 55));
        r.SetProperty("BackgroundColorDown",  Rgb(115, 30, 30));
        r.SetProperty("TextColor",            Rgb(255, 225, 225));
        r.SetProperty("BorderColor",          Rgb(155, 50, 50));
        r.SetProperty("Borders",              "1");
    }

    public static void ApplyEditBox(TGUI.WidgetRenderer r)
    {
        r.SetProperty("BackgroundColor",        Rgb(42, 46, 56));
        r.SetProperty("BackgroundColorHover",   Rgb(50, 54, 66));
        r.SetProperty("BackgroundColorDisabled",Rgb(35, 38, 46));
        r.SetProperty("TextColor",              Rgb(215, 218, 228));
        r.SetProperty("DefaultTextColor",       Rgb(105, 110, 124));
        r.SetProperty("TextColorDisabled",      Rgb(100, 104, 114));
        r.SetProperty("SelectedTextBackgroundColor", Rgb(55, 90, 150));
        r.SetProperty("BorderColor",            Rgb(65, 72, 90));
        r.SetProperty("BorderColorHover",       Rgb(85, 94, 115));
        r.SetProperty("BorderColorFocused",     Rgb(95, 148, 220));
        r.SetProperty("BorderColorDisabled",    Rgb(50, 54, 65));
        r.SetProperty("Borders",                "1");
        r.SetProperty("Padding",                "(6, 0, 6, 0)");
    }

    public static void ApplyLabel(TGUI.WidgetRenderer r,
        int textR = 195, int textG = 200, int textB = 212)
    {
        r.SetProperty("TextColor", Rgb(textR, textG, textB));
    }

    public static void ApplyListView(TGUI.WidgetRenderer r)
    {
        r.SetProperty("BackgroundColor",           Rgb(28, 30, 37));
        r.SetProperty("BackgroundColorHover",      Rgb(40, 43, 54));
        r.SetProperty("SelectedBackgroundColor",   Rgb(52, 88, 145));
        r.SetProperty("SelectedBackgroundColorHover", Rgb(62, 100, 160));
        r.SetProperty("HeaderBackgroundColor",     Rgb(35, 38, 48));
        r.SetProperty("TextColor",                 Rgb(195, 200, 210));
        r.SetProperty("TextColorHover",            Rgb(215, 220, 230));
        r.SetProperty("SelectedTextColor",         Rgb(230, 235, 255));
        r.SetProperty("HeaderTextColor",           Rgb(175, 185, 210));
        r.SetProperty("GridLinesColor",            Rgb(44, 47, 58));
        r.SetProperty("BorderColor",               Rgb(50, 54, 65));
        r.SetProperty("Borders",                   "1");
        r.SetProperty("ScrollbarWidth",            "12");
    }

    public static void ApplyListBox(TGUI.WidgetRenderer r)
    {
        r.SetProperty("BackgroundColor",           Rgb(28, 30, 37));
        r.SetProperty("BackgroundColorHover",      Rgb(40, 44, 55));
        r.SetProperty("SelectedBackgroundColor",   Rgb(52, 88, 145));
        r.SetProperty("SelectedBackgroundColorHover", Rgb(62, 100, 160));
        r.SetProperty("TextColor",                 Rgb(195, 200, 210));
        r.SetProperty("TextColorHover",            Rgb(215, 220, 230));
        r.SetProperty("SelectedTextColor",         Rgb(228, 232, 250));
        r.SetProperty("BorderColor",               Rgb(50, 54, 65));
        r.SetProperty("Borders",                   "1");
        r.SetProperty("ScrollbarWidth",            "10");
    }

    public static void ApplyCheckBox(TGUI.WidgetRenderer r)
    {
        r.SetProperty("TextColor",        Rgb(175, 180, 192));
        r.SetProperty("TextColorHover",   Rgb(200, 205, 218));
        r.SetProperty("CheckColor",       Rgb(110, 185, 255));
        r.SetProperty("CheckColorHover",  Rgb(130, 200, 255));
        r.SetProperty("BackgroundColor",  Rgb(42, 46, 57));
        r.SetProperty("BackgroundColorHover", Rgb(52, 57, 70));
        r.SetProperty("BorderColor",      Rgb(78, 86, 108));
        r.SetProperty("BorderColorHover", Rgb(95, 105, 130));
        r.SetProperty("Borders",          "1");
    }

    public static void ApplyChatBox(TGUI.WidgetRenderer r)
    {
        r.SetProperty("BackgroundColor", Rgb(24, 26, 32));
        r.SetProperty("BorderColor",     Rgb(48, 52, 64));
        r.SetProperty("Borders",         "1");
        r.SetProperty("Padding",         "(8, 6, 8, 6)");
        r.SetProperty("ScrollbarWidth",  "12");
    }

    public static void ApplyComboBox(TGUI.WidgetRenderer r)
    {
        r.SetProperty("BackgroundColor",      Rgb(42, 46, 57));
        r.SetProperty("ArrowBackgroundColor", Rgb(50, 55, 68));
        r.SetProperty("ArrowBackgroundColorHover", Rgb(62, 68, 84));
        r.SetProperty("ArrowColor",           Rgb(180, 185, 200));
        r.SetProperty("ArrowColorHover",      Rgb(210, 215, 230));
        r.SetProperty("TextColor",            Rgb(200, 205, 218));
        r.SetProperty("BorderColor",          Rgb(65, 72, 90));
        r.SetProperty("Borders",              "1");
    }

    public static void ApplyChildWindow(TGUI.WidgetRenderer r)
    {
        r.SetProperty("BackgroundColor", Rgb(33, 36, 44));
        r.SetProperty("TitleBarColor",   Rgb(40, 45, 58));
        r.SetProperty("TitleColor",      Rgb(200, 210, 232));
        r.SetProperty("BorderColor",     Rgb(62, 68, 84));
        r.SetProperty("Borders",         "1");
        r.SetProperty("BorderBelowTitleBar", "1");
        r.SetProperty("DistanceToSide",  "5");
    }

    /// <summary>
    /// Applies theme to a TabContainer's internal tab bar.
    ///
    /// MUST be called on <c>tabContainer.TabsRenderer</c> — that is the
    /// WidgetRenderer of the embedded Tabs widget.  The property names are
    /// case-sensitive and must exactly match the TGUI 1.x TabsRenderer setter
    /// names with the "set" prefix removed:
    ///
    ///   BackgroundColor         — unselected, non-hovered tab background
    ///   BackgroundColorHover    — unselected tab when mouse is over it
    ///   BackgroundColorDisabled — disabled tab background
    ///   SelectedBackgroundColor      — selected (active) tab background
    ///   SelectedBackgroundColorHover — selected tab when mouse is over it
    ///   TextColor               — unselected tab text
    ///   TextColorHover          — unselected tab text on hover
    ///   TextColorDisabled       — disabled tab text
    ///   SelectedTextColor       — selected tab text
    ///   BorderColor             — border around the entire tab bar
    ///   Borders                 — border thickness (Outline format)
    ///   DistanceToSide          — padding between tab text and tab edges
    ///
    /// The content Panel returned by AddTab() has its own separate Renderer
    /// and must be styled independently via panel.Renderer.SetProperty(...).
    /// </summary>
    public static void ApplyTabContainer(TGUI.WidgetRenderer r)
    {
        /*r.SetProperty("BackgroundColor",              Rgb(38, 42, 54));
        r.SetProperty("BackgroundColorHover",         Rgb(52, 57, 72));
        r.SetProperty("BackgroundColorDisabled",      Rgb(30, 33, 42));
        r.SetProperty("SelectedBackgroundColor",      Rgb(55, 95, 158));
        r.SetProperty("SelectedBackgroundColorHover", Rgb(65, 108, 178));
        r.SetProperty("TextColor",                    Rgb(160, 168, 188));
        r.SetProperty("TextColorHover",               Rgb(198, 205, 225));
        r.SetProperty("TextColorDisabled",            Rgb(90, 96, 112));
        r.SetProperty("SelectedTextColor",            Rgb(220, 228, 252));
        r.SetProperty("BorderColor",                  Rgb(45, 50, 64));
        r.SetProperty("Borders",                      "(0, 0, 0, 2)");
        r.SetProperty("DistanceToSide",               "10");*/
    }
}
