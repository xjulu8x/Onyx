using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopOverlay;

/// <summary>
/// A desktop card of the terminal look: a dark sheet with a fine dot grid, a thin line around it and a mono caption
/// ("+ JUNIPER", "SIGNAL / 01"). It sits at desktop level like the other widgets and can be arranged with them.
/// </summary>
public abstract class Card : Window
{
    public string Key { get; }
    protected readonly TextBlock Caption = Theme.Tag("");
    protected readonly StackPanel Meta = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    protected readonly Border Frame;
    private readonly DockPanel _body = new();
    private readonly Grid _header = new() { Margin = new Thickness(0, 0, 0, 10) };
    private readonly List<DispatcherTimer> _timers = new();

    protected Card(string key, string caption, double width, double height)
    {
        Key = key;
        Title = "Onyx-" + key;
        WindowStyle = WindowStyle.None;
        // Opaque rectangles need no per-pixel transparency: a normal window is drawn by the GPU, a transparent one is
        // recomposed in software on every frame, which made the moving cards several times more expensive.
        AllowsTransparency = false;
        Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0A));
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Foreground = Brushes.White;
        SetResourceReference(FontFamilyProperty, "Ui");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);

        Caption.Text = caption.ToUpperInvariant();
        Caption.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = this });
        _header.Children.Add(Caption);
        _header.Children.Add(Meta);
        DockPanel.SetDock(_header, Dock.Top);
        _body.Children.Add(_header);
        Frame = new Border
        {
            Width = width, Height = height, SnapsToDevicePixels = true, Background = Theme.Res("GlassPanel"), BorderBrush = Theme.Res("GlassRim"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), Padding = new Thickness(14, 10, 14, 12), Child = _body,
        };
        Content = Frame;

        Native.MakeOverlay(this, desktopLevel: true);
        WidgetMover.Attach(this, key);
        Loaded += (_, _) =>
        {
            var at = DefaultPlace(SystemParameters.WorkArea);
            Left = at.X;
            Top = at.Y;
        };
        IsVisibleChanged += (_, _) => Pulse.Refresh();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);

    /// <summary>Whole-window opacity for the "Deckkraft" setting; the window stays GPU-drawn.</summary>
    public void SetAlpha(double opacity)
    {
        var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) { SourceInitialized += (_, _) => SetAlpha(opacity); return; }
        int style = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
        const int layered = 0x80000;
        if (opacity >= 0.995) { Native.SetWindowLong(h, Native.GWL_EXSTYLE, style & ~layered); return; }
        Native.SetWindowLong(h, Native.GWL_EXSTYLE, style | layered);
        SetLayeredWindowAttributes(h, 0, (byte)(255 * opacity), 2 /* LWA_ALPHA */);
    }

    protected void SetBody(UIElement body) => _body.Children.Add(body);

    protected void HideHeader() => _header.Visibility = Visibility.Collapsed;

    /// <summary>Where the card goes before the user has placed it.</summary>
    protected abstract Point DefaultPlace(Rect work);

    /// <summary>
    /// A repeating update that only runs while the card can be seen: not while it is hidden, a game or video is full screen,
    /// or a maximized window covers the desktop. So nothing on the desktop costs anything while you play.
    /// </summary>
    protected void Every(TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
        timer.Tick += (_, _) => tick();
        _timers.Add(timer);
        Pulse.Register(this);
    }

    internal void Run(bool on)
    {
        foreach (var t in _timers) t.IsEnabled = on;
        if (on) OnResume();
    }

    /// <summary>Called when the card starts moving again, e.g. to redraw what changed meanwhile.</summary>
    protected virtual void OnResume() { }

    /// <summary>Mono key like "F  FEED" in the style of the reference widgets.</summary>
    protected static Button Hotkey(string key, string label, Action click)
    {
        var b = new Button { Style = (Style)Application.Current.Resources["Key"], Content = $"{key}  {label.ToUpperInvariant()}", Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(6, 4, 6, 4), FontSize = 9.5 };
        b.Click += (_, _) => click();
        return b;
    }

    protected static TextBlock Mono(string text = "", double size = 10, double opacity = 1) => new()
    {
        Text = text, FontFamily = Theme.Mono, FontSize = size, Opacity = opacity,
    };

    /// <summary>A block of monospaced text with an exact line height, for the ASCII scenes.</summary>
    protected static TextBlock Screen(double size = 10) => new()
    {
        FontFamily = Theme.Mono, FontSize = size, LineHeight = Math.Round(size * 1.2), LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        TextWrapping = TextWrapping.NoWrap, IsHitTestVisible = false,
    };
}

/// <summary>Decides whether the moving cards may run: widgets shown, nothing full screen, no maximized window over the desktop.</summary>
internal static class Pulse
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr h, out int pid);

    private static readonly List<Card> Cards = new();
    private static readonly int Own = Environment.ProcessId;
    private static bool _fullscreen, _covered, _started;
    private static DispatcherTimer _watch;

    public static void Register(Card card)
    {
        if (!Cards.Contains(card)) Cards.Add(card);
        if (_started) return;
        _started = true;
        TaskbarWindow.FullscreenChanged += full => { _fullscreen = full; Refresh(); };
        // A cheap look once a second whether a maximized window hides the desktop.
        _watch = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            var front = GetForegroundWindow();
            GetWindowThreadProcessId(front, out var pid);
            bool covered = pid != Own && IsZoomed(front);
            if (covered == _covered) return;
            _covered = covered;
            Refresh();
        }, Application.Current.Dispatcher);
    }

    /// <summary>Test aid: run regardless of what is in front.</summary>
    public static bool Force;

    public static bool Active => Force || !_fullscreen && !_covered && Settings.Current.ShowWidgets;

    public static void Refresh()
    {
        foreach (var card in Cards) card.Run(Active && card.IsVisible);
    }
}

/// <summary>Helpers for the ASCII scenes.</summary>
internal static class Ascii
{
    /// <summary>A text gauge like "[||||......]".</summary>
    public static string Gauge(double share, int cells = 6)
    {
        int full = (int)Math.Round(Math.Clamp(share, 0, 1) * cells);
        return "[" + new string('|', full) + new string('.', cells - full) + "]";
    }

    public static string Join(char[,] grid)
    {
        int rows = grid.GetLength(0), cols = grid.GetLength(1);
        var sb = new System.Text.StringBuilder(rows * (cols + 1));
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++) sb.Append(grid[r, c] == '\0' ? ' ' : grid[r, c]);
            if (r < rows - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    public static void Clear(char[,] grid)
    {
        for (int r = 0; r < grid.GetLength(0); r++)
            for (int c = 0; c < grid.GetLength(1); c++) grid[r, c] = ' ';
    }

    public static string Uptime()
    {
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return $"{(int)up.TotalHours:00}:{up.Minutes:00}:{up.Seconds:00}";
    }

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
}
