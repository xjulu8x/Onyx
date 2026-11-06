using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

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

    /// <summary>Background of a surface: plain, or a tiled fine grain. Either way it is one static brush with no running cost.</summary>
    public static Brush Surface(string hex, byte alpha)
    {
        var color = Settings.ToColor(hex, alpha, Color.FromRgb(0x06, 0x06, 0x08));
        if (!Settings.Current.Grain)
        {
            // Dark glass: a touch lighter at the top, like light falling on it.
            var top = Color.FromArgb(color.A, (byte)Math.Min(255, color.R + 22), (byte)Math.Min(255, color.G + 22), (byte)Math.Min(255, color.B + 24));
            var glass = new LinearGradientBrush(top, color, 90);
            glass.Freeze();
            return glass;
        }
        const int size = 128;
        var random = new Random(7); // same pattern every time
        var px = new byte[size * size * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            int noise = random.Next(-9, 10);
            px[i] = (byte)Math.Clamp(color.B + noise, 0, 255);
            px[i + 1] = (byte)Math.Clamp(color.G + noise, 0, 255);
            px[i + 2] = (byte)Math.Clamp(color.R + noise, 0, 255);
            px[i + 3] = alpha;
        }
        var tile = System.Windows.Media.Imaging.BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, px, size * 4);
        tile.Freeze();
        var brush = new ImageBrush(tile) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, size, size), Stretch = Stretch.None };
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush _lightRun;

    /// <summary>Rim brush and width. "subtle" is the quiet default of the taskbar; the island has no rim in that style.</summary>
    public static (Brush Brush, double Width) Rim(bool subtle)
    {
        var accent = Accent;
        switch (Settings.Current.RimStyle)
        {
            case 1 when subtle: return (new SolidColorBrush(Color.FromArgb(0x1F, 255, 255, 255)), 1);
            case 2: return (new SolidColorBrush(Color.FromArgb(0xA0, accent.R, accent.G, accent.B)), 1);
            case 3: return (LightRun(), 1);
            default: return (Brushes.Transparent, 0);
        }
    }

    /// <summary>A soft highlight turning slowly around the edge. Limited to 12 frames per second; hidden windows don't redraw it.</summary>
    private static Brush LightRun()
    {
        var accent = Accent;
        if (_lightRun == null)
        {
            var rotate = new RotateTransform(0, 0.5, 0.5);
            _lightRun = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5), RelativeTransform = rotate };
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(9)) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(spin, 12);
            rotate.BeginAnimation(RotateTransform.AngleProperty, spin);
        }
        _lightRun.GradientStops.Clear();
        _lightRun.GradientStops.Add(new GradientStop(Color.FromArgb(0x18, 255, 255, 255), 0));
        _lightRun.GradientStops.Add(new GradientStop(Color.FromArgb(0xE0, accent.R, accent.G, accent.B), 0.5));
        _lightRun.GradientStops.Add(new GradientStop(Color.FromArgb(0x18, 255, 255, 255), 1));
        return _lightRun;
    }

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

    // ---------- motion ----------

    /// <summary>Spring of the island: opening follows the chosen style, closing settles without bouncing.</summary>
    public static (float Damping, TimeSpan Period) Spring(bool open)
    {
        double fast = Settings.Current.AnimationSpeed == 0 ? 0.7 : 1;
        var (damping, ms) = !open ? (1f, 42.0) : Settings.Current.MotionStyle switch { 1 => (0.62f, 62.0), 2 => (1f, 38.0), _ => (0.8f, 58.0) };
        return (damping, TimeSpan.FromMilliseconds(ms * fast));
    }

    // ---------- own themes ----------

    // Everything a theme carries, with the allowed range of the numbers: theme files come from other people.
    private static readonly Dictionary<string, (double Min, double Max)> DesignKeys = new()
    {
        ["IslandColor"] = default, ["TaskbarColor"] = default, ["WidgetColor"] = default, ["AccentColor"] = default, ["FontFamily"] = default,
        ["TaskbarAppsLeft"] = default, ["Grain"] = default,
        ["AnimationSpeed"] = (0, 2), ["TaskbarTransparency"] = (0, 1), ["TaskbarHeight"] = (38, 64), ["TaskbarIconSize"] = (18, 32),
        ["TaskbarRadius"] = (0, 25), ["IslandScale"] = (0.8, 1.2), ["IslandCorners"] = (0.2, 1), ["IslandOpacity"] = (0.4, 1),
        ["WidgetScale"] = (0.7, 1.3), ["WidgetOpacity"] = (0.3, 1), ["TaskbarShape"] = (0, 2), ["IconTint"] = (0, 3),
        ["IndicatorStyle"] = (0, 3), ["RimStyle"] = (0, 3), ["ClockStyle"] = (0, 2), ["IslandShape"] = (0, 2), ["MotionStyle"] = (0, 2), ["WidgetGraph"] = (0, 2),
    };

    /// <summary>All theme names: the built-in ones, then the user's own.</summary>
    public static List<string> Names() => Presets.Select(p => p.Name).Concat(Settings.Current.CustomThemes.Keys).ToList();

    public static void Use(string name)
    {
        int preset = Array.FindIndex(Presets, p => p.Name == name);
        if (preset >= 0) UsePreset(preset);
        else if (Settings.Current.CustomThemes.TryGetValue(name, out var values)) { Load(values); Settings.Current.Save(); ApplyAll(); }
    }

    public static void SaveCurrent(string name)
    {
        Settings.Current.CustomThemes[name] = Capture();
        Settings.Current.Save();
    }

    private static void Load(Dictionary<string, JsonElement> values)
    {
        foreach (var (key, value) in values)
        {
            if (!DesignKeys.TryGetValue(key, out var range)) continue; // unknown entries in a file are ignored
            var property = typeof(Settings).GetProperty(key)!;
            try
            {
                var parsed = value.Deserialize(property.PropertyType);
                if (parsed == null) continue;
                if (parsed is double d) parsed = Math.Clamp(d, range.Min, range.Max);
                if (parsed is int i) parsed = (int)Math.Clamp(i, range.Min, range.Max);
                if (key.EndsWith("Color") && parsed is string hex && !System.Text.RegularExpressions.Regex.IsMatch(hex, "^#[0-9A-Fa-f]{6}$")) continue;
                property.SetValue(Settings.Current, parsed);
            }
            catch (JsonException) { }
        }
    }

    public static void Export(string name, string file) =>
        File.WriteAllText(file, JsonSerializer.Serialize(Settings.Current.CustomThemes[name], new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>Adds a theme file to the own themes and returns its name, or null when the file is not a theme.</summary>
    public static string Import(string file)
    {
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(file));
            if (values == null || !values.Keys.Any(DesignKeys.ContainsKey)) return null;
            var name = Path.GetFileNameWithoutExtension(file);
            Settings.Current.CustomThemes[name] = values;
            Settings.Current.Save();
            return name;
        }
        catch { return null; }
    }

    // ---------- profiles ----------

    private static string _profile;
    private static Dictionary<string, JsonElement> _beforeProfile;

    /// <summary>
    /// Called with the program in the foreground: its theme is applied while it is in front, then the previous design
    /// returns. Profile themes are never saved, so a crash can't leave one behind as the normal design.
    /// </summary>
    public static void CheckProfile(string exePath)
    {
        var s = Settings.Current;
        var program = exePath == null ? null : Path.GetFileName(exePath);
        var theme = program != null && s.Profiles.TryGetValue(program, out var t) ? t : null;
        if (theme == _profile) return;

        if (_beforeProfile != null && theme == null)
        {
            Load(_beforeProfile);
            _beforeProfile = null;
        }
        else if (theme != null)
        {
            _beforeProfile ??= Capture();
            int preset = Array.FindIndex(Presets, p => p.Name == theme);
            if (preset >= 0)
            {
                var p = Presets[preset];
                (s.IslandColor, s.TaskbarColor, s.WidgetColor, s.AccentColor, s.TaskbarTransparency) = (p.Island, p.Taskbar, p.Widgets, p.Accent, p.Transparency);
            }
            else if (s.CustomThemes.TryGetValue(theme, out var values)) Load(values);
        }
        _profile = theme;
        ApplyAll();
    }

    /// <summary>The design to write to disk: while a profile is active that is the one from before it.</summary>
    public static void RestoreForSave(Action save)
    {
        if (_beforeProfile == null) { save(); return; }
        var current = Capture();
        Load(_beforeProfile);
        save();
        Load(current);
    }

    private static Dictionary<string, JsonElement> Capture() =>
        DesignKeys.Keys.ToDictionary(key => key, key => JsonSerializer.SerializeToElement(typeof(Settings).GetProperty(key)!.GetValue(Settings.Current)));

    /// <summary>A bright enough average color of the cover, for the music glow.</summary>
    public static Color CoverColor(System.Windows.Media.Imaging.BitmapSource art)
    {
        var small = new System.Windows.Media.Imaging.TransformedBitmap(art, new ScaleTransform(16.0 / art.PixelWidth, 16.0 / art.PixelHeight));
        var bgra = new System.Windows.Media.Imaging.FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        var px = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(px, stride, 0);
        double b = 0, g = 0, r = 0;
        int n = px.Length / 4;
        for (int i = 0; i < px.Length; i += 4) { b += px[i]; g += px[i + 1]; r += px[i + 2]; }
        b /= n; g /= n; r /= n;
        double lift = 200 / Math.Max(1, Math.Max(r, Math.Max(g, b)));
        return Color.FromRgb((byte)Math.Min(255, r * lift), (byte)Math.Min(255, g * lift), (byte)Math.Min(255, b * lift));
    }

    // ---------- time of day ----------

    private static DispatcherTimer _clock;
    private static bool? _wasDay;

    /// <summary>Switches between the day and night theme at 7:00 and 19:00. The check runs once a minute.</summary>
    public static void StartAutoTheme()
    {
        _clock = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _clock.Tick += (_, _) => CheckTimeOfDay();
        _clock.Start();
        CheckTimeOfDay();
    }

    public static void CheckTimeOfDay(bool force = false)
    {
        var s = Settings.Current;
        if (!s.AutoTheme) { _wasDay = null; return; }
        bool day = DateTime.Now.Hour is >= 7 and < 19;
        if (day == _wasDay && !force) return;
        _wasDay = day;
        Use(day ? s.DayTheme : s.NightTheme);
    }
}
