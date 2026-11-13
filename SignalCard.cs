using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopOverlay;

/// <summary>
/// "SIGNAL / 01": a round dot-matrix display. FACE shows two eyes that follow the mouse and blink, PULSE a ring that beats
/// with the music, CLOCK the time in big dots. Next to it a few live readouts.
/// </summary>
public sealed class SignalCard : Card
{
    private static readonly string[] Modes = { "Face", "Pulse", "Clock" };
    private readonly DotFace _face = new() { Width = 156, Height = 156 };
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _readout = Mono("", 9, 0.8);

    public SignalCard() : base("Signal", "", 330, 236)
    {
        var dot = new TextBlock { Text = "● ", Foreground = Theme.Res("Signal"), FontSize = 8, VerticalAlignment = VerticalAlignment.Center };
        Caption.Text = "SIGNAL / 01";
        Caption.Margin = new Thickness(12, 0, 0, 0);
        dot.HorizontalAlignment = HorizontalAlignment.Left;
        ((Grid)Caption.Parent).Children.Add(dot);
        for (int i = 0; i < Modes.Length; i++)
        {
            int index = i;
            var tab = new Border { Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(3, 0, 0, 0), CornerRadius = new CornerRadius(6), BorderBrush = Theme.Res("Line"), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Child = Mono(Modes[i].ToUpperInvariant(), 8) };
            tab.MouseLeftButtonUp += (_, _) => Switch(index);
            _tabs.Children.Add(tab);
        }
        Meta.Children.Add(_tabs);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var disc = new Border { Width = 168, Height = 168, CornerRadius = new CornerRadius(84), BorderBrush = Theme.Res("LineHi"), BorderThickness = new Thickness(1), Background = new SolidColorBrush(Color.FromArgb(0x0A, 255, 255, 255)), Child = _face };
        row.Children.Add(disc);
        _readout.Margin = new Thickness(16, 6, 0, 0);
        _readout.LineHeight = 15;
        Grid.SetColumn(_readout, 1);
        row.Children.Add(_readout);
        SetBody(row);
        Switch(Math.Clamp(Settings.Current.SignalMode, 0, 2), save: false);
        Every(TimeSpan.FromMilliseconds(83), Tick);
    }

    private void Switch(int mode, bool save = true)
    {
        Settings.Current.SignalMode = mode;
        if (save) Settings.Current.Save();
        _face.Mode = mode;
        _face.Reset();
        for (int i = 0; i < _tabs.Children.Count; i++)
        {
            var tab = (Border)_tabs.Children[i];
            tab.Background = i == mode ? new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)) : Brushes.Transparent;
            ((TextBlock)tab.Child).Foreground = i == mode ? Theme.Res("Ink") : Foreground;
        }
        _face.InvalidateVisual();
    }

    private int _frame;

    private void Tick()
    {
        _frame++;
        var media = MediaService.Instance;
        double level = media.IsPlaying ? Math.Sqrt(Volume.Peak()) : 0;
        _face.Step(level, PointToScreen(new Point(98, 116))); // middle of the disc
        if (_frame % 6 != 1) return;
        var sys = SystemMonitor.Instance;
        _readout.Text =
            $"LVL {Ascii.Gauge(level, 8)}\n" +
            $"CPU {Ascii.Gauge(sys.Cpu / 100, 8)}\n" +
            $"RAM {Ascii.Gauge(sys.RamTotalGB > 0 ? sys.RamUsedGB / sys.RamTotalGB : 0, 8)}\n" +
            $"NET {SystemMonitor.Rate(sys.DownBps)}\n" +
            $"\n{DateTime.Now:HH:mm:ss}\n" +
            (media.HasSession ? (media.IsPlaying ? "|| " : "> ") + Trim(media.Title, 16) : "KEIN SIGNAL");
    }

    private static string Trim(string s, int n) => s.Length > n ? s[..(n - 1)] + "…" : s;

    protected override void OnResume() => Tick();

    protected override Point DefaultPlace(Rect w) => new(w.Right - 798, w.Bottom - 248);
}

/// <summary>Draws the round dot display; one dot every 6 pixels, lit dots in the widget color.</summary>
public sealed class DotFace : FrameworkElement
{
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Native.POINT p);

    private const double Pitch = 6, Dot = 4;
    private static readonly Brush Off = Freeze(new SolidColorBrush(Color.FromArgb(0x10, 255, 255, 255)));
    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    private readonly Random _random = new();
    private double _lookX, _lookY, _level, _blink, _ring;
    private int _nextBlink = 40;
    private (double, double, double, double) _drawn;

    public int Mode;

    public void Reset() => _drawn = (double.NaN, 0, 0, 0);

    public void Step(double level, Point center)
    {
        _level = level;
        // Eyes: look toward the mouse, a little each frame, and blink now and then.
        if (GetCursorPos(out var p))
        {
            double dx = p.X - center.X, dy = p.Y - center.Y, len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            double reach = Math.Min(1, len / 400);
            _lookX += (dx / len * reach * 2 - _lookX) * 0.25;
            _lookY += (dy / len * reach * 1.5 - _lookY) * 0.25;
        }
        if (--_nextBlink <= 0) { _blink = 1; _nextBlink = 30 + _random.Next(70); }
        _blink = Math.Max(0, _blink - 0.25);
        _ring = (_ring + 0.4 + level * 1.8) % 14;
        // Redraw only when the picture changes: still eyes and a quiet room cost nothing.
        var look = Mode switch
        {
            0 => (Math.Round(_lookX * 4), Math.Round(_lookY * 4), Math.Round(_blink * 4), Math.Round(_level * 8)),
            1 => (Math.Round(_ring * 2), Math.Round(_level * 8), 0, 0),
            _ => (DateTime.Now.Minute, DateTime.Now.Second % 2, 0, 0),
        };
        if (look == _drawn) return;
        _drawn = look;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var on = (Brush)GetValue(System.Windows.Documents.TextElement.ForegroundProperty) ?? Brushes.White;
        int n = (int)(ActualWidth / Pitch);
        double r = n / 2.0;
        var time = DateTime.Now.ToString("HHmm");
        for (int row = 0; row < n; row++)
            for (int col = 0; col < n; col++)
            {
                double x = col - r + 0.5, y = row - r + 0.5;
                if (x * x + y * y > (r - 0.5) * (r - 0.5)) continue;
                bool lit = Mode switch
                {
                    0 => Eye(x, y),
                    1 => Pulse(x, y),
                    _ => Clock(time, col - (n - 17) / 2, row - (n - 5) / 2),
                };
                dc.DrawRectangle(lit ? on : Off, null, new Rect(col * Pitch + 1, row * Pitch + 1, Dot, Dot));
            }
    }

    /// <summary>Two rounded eye blocks; blinking squeezes them to a line, music makes them grow a little.</summary>
    private bool Eye(double x, double y)
    {
        double height = 4.6 * (1 - _blink * 0.9) + _level * 1.2, width = 2.4 + _level * 0.6;
        foreach (double cx in new[] { -4.4, 4.4 })
        {
            double ex = (x - cx - _lookX) / width, ey = (y - _lookY) / height;
            if (Math.Pow(Math.Abs(ex), 3) + Math.Pow(Math.Abs(ey), 3) <= 1) return true;
        }
        return false;
    }

    private bool Pulse(double x, double y)
    {
        double d = Math.Sqrt(x * x + y * y);
        return Math.Abs(d - _ring) < 0.7 || Math.Abs(d - (_ring + 7) % 14) < 0.5 || d < 1.5 + _level * 4;
    }

    // 3x5 digits for the clock mode.
    private static readonly string[] Digits =
    {
        "111101101101111", "010110010010111", "111001111100111", "111001111001111", "101101111001001",
        "111100111001111", "111100111101111", "111001010010010", "111101111101111", "111101111001111",
    };

    private static bool Clock(string time, int col, int row)
    {
        if (row < 0 || row >= 5 || col < 0) return false;
        // HH:MM with one dot of space between digits and a colon in the middle.
        int[] starts = { 0, 4, 10, 14 };
        if (col == 8 && row is 1 or 3) return DateTime.Now.Second % 2 == 0;
        for (int i = 0; i < 4; i++)
        {
            int c = col - starts[i];
            if (c is >= 0 and < 3) return Digits[time[i] - '0'][row * 3 + c] == '1';
        }
        return false;
    }
}
