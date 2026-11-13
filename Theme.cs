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
    private static readonly Color DefaultAccent = Color.FromRgb(0xE6, 0xE6, 0xE6);

    public static readonly (string Label, string Family)[] Fonts =
    {
        ("Standard", "Segoe UI Variable Display, Segoe UI"),
        ("Cascadia Mono", "Cascadia Mono, Consolas"),
        ("Bahnschrift", "Bahnschrift"),
        ("Corbel", "Corbel"),
        ("Consolas", "Consolas"),
    };

    public static readonly string[] AccentColors = { "#E6E6E6", "#30D158", "#FF453A", "#FFB340" };

    public static readonly (string Name, string Island, string Taskbar, string Widgets, string Accent, double Transparency)[] Presets =
    {
        ("Mono", "#0A0A0A", "#0A0A0A", "#E6E6E6", "#E6E6E6", 0.04),
        ("Phosphor", "#070A08", "#070A08", "#D8F5DF", "#30D158", 0.04),
        ("Signal", "#0A0A0A", "#0A0A0A", "#E6E6E6", "#FF453A", 0.04),
        ("Bernstein", "#0B0906", "#0B0906", "#F2E3C6", "#FFB340", 0.04),
    };

    public static Color Accent => Settings.ToColor(Settings.Current.AccentColor, 0xFF, DefaultAccent);
    public static Brush AccentBrush => new SolidColorBrush(Accent);

    /// <summary>Multiplier for animation lengths; 0 turns animations off.</summary>
    public static double Speed => Settings.Current.AnimationSpeed switch { 0 => 0.6, 2 => 0, _ => 1 };

    /// <summary>Windows' "Animationseffekte" are off: no growing or sliding, only short cross-fades.</summary>
    public static bool ReduceMotion => !SystemParameters.ClientAreaAnimation;
    public static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms * Speed);

    // ---------- shared motion (Apple: settle without bouncing, start from where things are, leave the way they came) ----------

    /// <summary>The curve of a critically damped spring: quick start, long soft settle, no overshoot.</summary>
    public static readonly IEasingFunction Settle = Freeze(new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 });
    private static readonly IEasingFunction FadeCurve = Freeze(new QuadraticEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction LeaveCurve = Freeze(new QuadraticEase { EasingMode = EasingMode.EaseIn });

    private static IEasingFunction Freeze(EasingFunctionBase ease) { ease.Freeze(); return ease; }

    /// <summary>
    /// Brings a popup panel in from the side it belongs to (offsetY: negative = from above, positive = from below) and
    /// grows it slightly from its anchor. A panel that was still leaving continues from where it is instead of jumping.
    /// </summary>
    public static void Enter(UIElement panel, ScaleTransform zoom, TranslateTransform slide, double offsetY, bool fromHidden)
    {
        if (fromHidden)
        {
            panel.BeginAnimation(UIElement.OpacityProperty, null);
            panel.Opacity = 0;
            foreach (var p in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty }) zoom.BeginAnimation(p, null);
            slide.BeginAnimation(TranslateTransform.YProperty, null);
            slide.Y = ReduceMotion ? 0 : offsetY;
            zoom.ScaleX = zoom.ScaleY = ReduceMotion ? 1 : 0.97;
        }
        panel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, Ms(150)) { EasingFunction = FadeCurve });
        var settle = new DoubleAnimation(ReduceMotion ? slide.Y : 0, Ms(280)) { EasingFunction = Settle };
        slide.BeginAnimation(TranslateTransform.YProperty, settle);
        var grow = new DoubleAnimation(1, Ms(280)) { EasingFunction = Settle };
        zoom.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        zoom.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    /// <summary>The way back: same direction, a little quicker. Returns the fade, so the caller can hide the window when it ends.</summary>
    public static DoubleAnimation Leave(UIElement panel, ScaleTransform zoom, TranslateTransform slide, double offsetY)
    {
        var fade = new DoubleAnimation(0, Ms(120)) { EasingFunction = LeaveCurve };
        if (!ReduceMotion)
        {
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offsetY * 0.6, Ms(140)) { EasingFunction = LeaveCurve });
            var shrink = new DoubleAnimation(0.98, Ms(140)) { EasingFunction = LeaveCurve };
            zoom.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            zoom.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        }
        panel.BeginAnimation(UIElement.OpacityProperty, fade);
        return fade;
    }

    /// <summary>Background of a surface: a flat dark sheet, with a fine dot grid when "Punktraster" is on. One static brush, no running cost.</summary>
    public static Brush Surface(string hex, byte alpha)
    {
        var color = Settings.ToColor(hex, alpha, Color.FromRgb(0x0A, 0x0A, 0x0A));
        if (!Settings.Current.Grain)
        {
            var flat = new SolidColorBrush(color);
            flat.Freeze();
            return flat;
        }
        return DotGrid(color);
    }

    /// <summary>The dot grid of the terminal look: one faint dot every 8 pixels on the given color.</summary>
    public static Brush DotGrid(Color color, byte dot = 0x18)
    {
        // An 8x8 tile with one dot, blended onto the color here, so the brush is a plain bitmap.
        const int size = 8;
        var px = new byte[size * size * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            bool isDot = i == 0;
            double k = isDot ? dot / 255.0 : 0;
            px[i] = (byte)(color.B + (255 - color.B) * k);
            px[i + 1] = (byte)(color.G + (255 - color.G) * k);
            px[i + 2] = (byte)(color.R + (255 - color.R) * k);
            px[i + 3] = color.A;
        }
        // Premultiplied, so the see-through bar stays correct.
        for (int i = 0; i < px.Length; i += 4)
            for (int c = 0; c < 3; c++) px[i + c] = (byte)(px[i + c] * color.A / 255);
        var tile = System.Windows.Media.Imaging.BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, px, size * 4);
        tile.Freeze();
        var brush = new ImageBrush(tile) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, size, size), Stretch = Stretch.None };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
        brush.Freeze();
        return brush;
    }

    // ---------- terminal look: shared pieces for code-built parts ----------

    public static FontFamily Mono => (FontFamily)Application.Current.Resources["Mono"];
    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>A framed card: faint fill, thin line, almost square corners.</summary>
    public static System.Windows.Controls.Border Card(UIElement child = null, Thickness? padding = null) => new()
    {
        Background = Res("Card"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(2), Padding = padding ?? new Thickness(12), Child = child,
    };

    /// <summary>Mono caption like "+ MUSIK / 01".</summary>
    public static System.Windows.Controls.TextBlock Tag(string text, double size = 10) => new()
    {
        Text = text.ToUpperInvariant(), FontFamily = Mono, FontSize = size, Foreground = Res("Dim"),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static LinearGradientBrush _lightRun;

    /// <summary>Rim brush and width. "subtle" is the quiet default of the taskbar; the island has no rim in that style.</summary>
    public static (Brush Brush, double Width) Rim(bool subtle)
    {
        var accent = Accent;
        switch (Settings.Current.RimStyle)
        {
            case 1: return (new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), 1);
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
        UsePresetValues(index);
        Settings.Current.Save();
        ApplyAll();
    }

    /// <summary>Only sets the values of a preset, without saving or redrawing.</summary>
    public static void UsePresetValues(int index)
    {
        var p = Presets[index];
        var s = Settings.Current;
        s.IslandColor = p.Island;
        s.TaskbarColor = p.Taskbar;
        s.WidgetColor = p.Widgets;
        s.AccentColor = p.Accent;
        s.TaskbarTransparency = p.Transparency;
    }

    // ---------- motion ----------

    /// <summary>Spring of the island: opening follows the chosen style, closing settles without bouncing.</summary>
    public static (float Damping, TimeSpan Period) Spring(bool open)
    {
        double fast = Settings.Current.AnimationSpeed == 0 ? 0.7 : 1;
        // Apple: sheets and drawers use a little less than critical damping (0.8); 0.88 gets the body to its full size quickly,
        // so the controls never stand out past it, with an overshoot of only about 1 %.
        // Measured against the reference video: open in about 150-200 ms, close in about 120 ms.
        var (damping, ms) = !open ? (1f, 40.0) : Settings.Current.MotionStyle switch { 1 => (0.75f, 60.0), 2 => (1f, 38.0), _ => (0.88f, 50.0) };
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
