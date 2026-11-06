using System.Windows;
using System.Windows.Media;

namespace DesktopOverlay;

/// <summary>Central place for the look: accent color, animation speed, fonts and the ready-made themes.</summary>
internal static class Theme
{
    private static readonly Color DefaultAccent = Color.FromRgb(0x2B, 0x8C, 0xFF);

    public static readonly (string Label, string Family)[] Fonts =
    {
        ("Standard", "Segoe UI Variable Display, Segoe UI"),
        ("Bahnschrift", "Bahnschrift"),
        ("Corbel", "Corbel"),
        ("Consolas", "Consolas"),
    };

    public static readonly string[] AccentColors = { "#2B8CFF", "#7FA6F0", "#B9BCC4", "#FFFFFF" };

    public static readonly (string Name, string Island, string Taskbar, string Widgets, string Accent, double Transparency)[] Presets =
    {
        ("Onyx", "#060608", "#060608", "#FFFFFF", "#2B8CFF", 0.12),
        ("Mitternacht", "#0B1A36", "#0B1A36", "#FFFFFF", "#7FA6F0", 0.10),
        ("Graphit", "#1E1F23", "#1E1F23", "#B9BCC4", "#B9BCC4", 0.08),
        ("Kontrast", "#050506", "#050506", "#FFFFFF", "#FFFFFF", 0.02),
    };

    public static Color Accent => Settings.ToColor(Settings.Current.AccentColor, 0xFF, DefaultAccent);
    public static Brush AccentBrush => new SolidColorBrush(Accent);

    /// <summary>Multiplier for animation lengths; 0 turns animations off.</summary>
    public static double Speed => Settings.Current.AnimationSpeed switch { 0 => 0.6, 2 => 0, _ => 1 };
    public static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms * Speed);

    public static void ApplyFont() => Application.Current.Resources["Ui"] = new FontFamily(Settings.Current.FontFamily);

    /// <summary>Applies every design setting to all windows.</summary>
    public static void ApplyAll()
    {
        var app = (App)Application.Current;
        ApplyFont();
        app.Taskbar.ApplyLook();
        app.Island.ApplyLook();
        app.ApplyWidgetLook();
    }

    public static void UsePreset(int index)
    {
        var p = Presets[index];
        var s = Settings.Current;
        s.IslandColor = p.Island;
        s.TaskbarColor = p.Taskbar;
        s.WidgetColor = p.Widgets;
        s.AccentColor = p.Accent;
        s.TaskbarTransparency = p.Transparency;
        s.Save();
        ApplyAll();
    }
}
