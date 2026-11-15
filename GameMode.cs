using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopOverlay;

/// <summary>
/// While a game is in front (full screen, recognized as a game): a small HUD with the important numbers in a corner,
/// and the game's own crosshair and saturation. Every game gets a profile the first time it runs; it is edited under
/// Einstellungen → Spiel. Leaving the game brings the desktop's own settings back.
/// </summary>
internal static class GameMode
{
    private static GameHud _hud;
    private static string _game;

    public static string Current => _game;

    public static void Start()
    {
        TaskbarWindow.FullscreenChanged += full => Application.Current.Dispatcher.BeginInvoke(() => { if (full) Enter(); else Leave(); });
    }

    private static void Enter()
    {
        var path = TaskbarApps.PathOf(TaskbarApps.ProcessOf(Win32.GetForegroundWindow()));
        if (path == null) return;
        var s = Settings.Current;
        string exe = Path.GetFileName(path);
        if (!s.GameProfiles.TryGetValue(exe, out var profile))
        {
            // First time: the game starts with what is set now.
            profile = new GameProfile { Name = Path.GetFileNameWithoutExtension(path), Crosshair = s.CrosshairOn };
            s.GameProfiles[exe] = profile;
            s.Save();
        }
        _game = exe;
        var app = (App)Application.Current;
        app.Crosshair.Show(profile.Crosshair);
        if (profile.Vibrance is { } level) Vibrance.Set(level);
        if (s.GameHud) { _hud ??= new GameHud(); _hud.Place(); _hud.Show(); }
    }

    private static void Leave()
    {
        if (_game == null) return;
        var s = Settings.Current;
        bool hadVibrance = s.GameProfiles.TryGetValue(_game, out var profile) && profile.Vibrance != null;
        _game = null;
        _hud?.Hide();
        var app = (App)Application.Current;
        app.Crosshair.Show(s.CrosshairOn && !s.CrosshairOnlyInGame);
        if (hadVibrance) Vibrance.Set(s.VibranceLevel ?? Vibrance.Default()); // the desktop's own colors again
    }

    /// <summary>The settings of a game changed while it may be running: apply them at once.</summary>
    public static void Refresh()
    {
        if (_game == null) { _hud?.Hide(); return; }
        var s = Settings.Current;
        if (s.GameProfiles.TryGetValue(_game, out var profile))
        {
            ((App)Application.Current).Crosshair.Show(profile.Crosshair);
            if (profile.Vibrance is { } level) Vibrance.Set(level);
        }
        if (s.GameHud) { _hud ??= new GameHud(); _hud.Place(); _hud.Show(); } else _hud?.Hide();
    }
}

/// <summary>
/// The in-game numbers: CPU load and temperature, GPU temperature, memory, ping and the time, in one small dark line.
/// Click-through, always on top, updated once a second. Over exclusive full screen no overlay can draw; borderless works.
/// </summary>
internal sealed class GameHud : Window
{
    private readonly TextBlock _text = new() { FontFamily = Theme.Mono, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)) };
    private readonly DispatcherTimer _raise = new() { Interval = TimeSpan.FromSeconds(2) };

    public GameHud()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "Onyx-HUD";
        var dot = new System.Windows.Shapes.Rectangle { Width = 5, Height = 5, Fill = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(dot);
        row.Children.Add(_text);
        Content = new Border { Background = new SolidColorBrush(Color.FromArgb(0xB4, 0x0A, 0x0A, 0x0A)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x50, 0x50, 0x50)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), Padding = new Thickness(9, 4, 10, 4), Child = row };
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.SetWindowLong(h, Native.GWL_EXSTYLE, Native.GetWindowLong(h, Native.GWL_EXSTYLE) | 0x20 | 0x80000 | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
        };
        SystemMonitor.Instance.Updated += () => Dispatcher.BeginInvoke(Update);
        _raise.Tick += (_, _) => Native.BringTopmost(this);
        IsVisibleChanged += (_, _) => { _raise.IsEnabled = IsVisible; if (IsVisible) Update(); };
        SizeChanged += (_, _) => Place();
    }

    private void Update()
    {
        if (!IsVisible) return;
        var m = SystemMonitor.Instance;
        string Temp(double? t) => t is { } v ? $"{v:0}°" : "-";
        _text.Text = $"CPU {m.Cpu:0}% {Temp(m.CpuTemp)}   GPU {Temp(m.GpuTemp)}   RAM {m.RamUsedGB:0.0}/{m.RamTotalGB:0}   PING {(m.PingMs is { } p ? $"{p:0}" : "-")}   {DateTime.Now:HH:mm}";
    }

    public void Place()
    {
        var screen = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        double w = ActualWidth > 0 ? ActualWidth : 380, h = ActualHeight > 0 ? ActualHeight : 24, m = 10;
        int corner = Settings.Current.GameHudCorner;
        Left = corner is 1 or 3 ? screen.Right - w - m : m;
        Top = corner is 2 or 3 ? screen.Bottom - h - m : m;
    }
}
