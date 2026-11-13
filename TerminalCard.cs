using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DesktopOverlay;

/// <summary>
/// A small terminal on the desktop with five scenes: fetch (system info), cava (level bars), life (Game of Life),
/// orb (a turning lit sphere) and rain (falling characters). Two text layers draw dim trails and bright heads.
/// </summary>
public sealed class TerminalCard : Card
{
    private const int Cols = 52, Rows = 17;
    private static readonly string[] Modes = { "fetch", "cava", "life", "orb", "rain" };
    private readonly char[,] _dim = new char[Rows, Cols], _bright = new char[Rows, Cols];
    private readonly TextBlock _dimText = Screen(9.5), _brightText = Screen(9.5);
    private readonly TextBlock _title = Mono("", 9.5), _command = Mono("", 9.5), _status = Mono("", 9, 0.6);
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    private readonly Random _random = new();
    private int _frame;

    public TerminalCard() : base("Terminal", "", 360, 318)
    {
        HideHeader();
        Frame.Padding = new Thickness(0);

        // Window bar: three dots, the title in the middle, the size on the right.
        var bar = new Grid { Height = 24, Background = new SolidColorBrush(Color.FromArgb(0x08, 255, 255, 255)) };
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var color in new[] { "#FF453A", "#3A3A3A", "#3A3A3A" })
            dots.Children.Add(new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 5, 0), Fill = (Brush)new BrushConverter().ConvertFrom(color) });
        bar.Children.Add(dots);
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.FontWeight = FontWeights.Bold;
        bar.Children.Add(_title);
        var size = Mono("zsh · 52x17", 8.5, 0.45);
        size.HorizontalAlignment = HorizontalAlignment.Right;
        size.VerticalAlignment = VerticalAlignment.Center;
        size.Margin = new Thickness(0, 0, 10, 0);
        bar.Children.Add(size);
        var barLine = new Border { BorderBrush = Theme.Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Child = bar };

        // Screen: dim trails below, bright heads on top, on exactly the same character grid.
        var screen = new Grid { Margin = new Thickness(12, 8, 12, 4), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        _dimText.VerticalAlignment = _brightText.VerticalAlignment = VerticalAlignment.Center;
        _dimText.Opacity = 0.45;
        screen.Children.Add(_dimText);
        screen.Children.Add(_brightText);
        screen.MouseLeftButtonUp += (_, _) => Burst();

        var prompt = new Grid { Margin = new Thickness(12, 0, 12, 6) };
        _command.Opacity = 0.85;
        prompt.Children.Add(_command);
        var hint = Mono("klick: burst", 8.5, 0.4);
        hint.HorizontalAlignment = HorizontalAlignment.Right;
        prompt.Children.Add(hint);

        var foot = new Grid { Margin = new Thickness(10, 0, 10, 8) };
        foot.Children.Add(_tabs);
        _status.HorizontalAlignment = HorizontalAlignment.Right;
        _status.VerticalAlignment = VerticalAlignment.Center;
        foot.Children.Add(_status);
        for (int i = 0; i < Modes.Length; i++)
        {
            int index = i;
            var tab = new Border { Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(0, 0, 3, 0), CornerRadius = new CornerRadius(1), Cursor = Cursors.Hand, Child = Mono($"{i + 1} {Modes[i]}", 8.5) };
            tab.MouseLeftButtonUp += (_, _) => Switch(index);
            _tabs.Children.Add(tab);
        }

        var stack = new DockPanel();
        DockPanel.SetDock(barLine, Dock.Top);
        stack.Children.Add(barLine);
        var lower = new StackPanel();
        lower.Children.Add(prompt);
        lower.Children.Add(new Border { Height = 1, Background = Theme.Res("Line"), Margin = new Thickness(0, 0, 0, 6) });
        lower.Children.Add(foot);
        DockPanel.SetDock(lower, Dock.Bottom);
        stack.Children.Add(lower);
        stack.Children.Add(screen);
        SetBody(stack);

        Switch(Math.Clamp(Settings.Current.TerminalMode, 0, Modes.Length - 1), save: false);
        Every(TimeSpan.FromMilliseconds(83), Tick);
    }

    private int Mode => Settings.Current.TerminalMode;

    /// <summary>Test aid: shows a scene without remembering it.</summary>
    public void Show(int mode) => Switch(mode, save: false);

    private void Switch(int mode, bool save = true)
    {
        Settings.Current.TerminalMode = mode;
        if (save) Settings.Current.Save();
        for (int i = 0; i < _tabs.Children.Count; i++)
        {
            var tab = (Border)_tabs.Children[i];
            bool on = i == mode;
            tab.Background = on ? new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)) : Brushes.Transparent;
            ((TextBlock)tab.Child).Foreground = on ? Theme.Res("Ink") : Foreground;
            ((TextBlock)tab.Child).Opacity = on ? 1 : 0.6;
        }
        _title.Text = $"user@onyx: ~ — {Modes[mode]}";
        _command.Text = "~/.config › " + mode switch { 0 => "onyxfetch", 1 => "cava", 2 => "life --random", 3 => "orb --spin", _ => "cmatrix -b" };
        Ascii.Clear(_dim);
        Ascii.Clear(_bright);
        _drops = null;
        _life = null;
        Tick();
    }

    private void Tick()
    {
        _frame++;
        switch (Mode)
        {
            case 0: if (_frame % 12 == 1) Fetch(); else return; break;
            case 1: Cava(); break;
            case 2: if (_frame % 2 == 0) Life(); else return; break;
            case 3: Orb(); break;
            default: Rain(); break;
        }
        _dimText.Text = Ascii.Join(_dim);
        _brightText.Text = Ascii.Join(_bright);
        if (_frame % 12 == 1)
        {
            var media = MediaService.Instance;
            string song = media.HasSession && media.Title.Length > 0 ? (media.IsPlaying ? "|| " : "> ") + media.Title : "";
            if (song.Length > 22) song = song[..21] + "…";
            _status.Text = $"{song}  up {Ascii.Uptime()}";
        }
    }

    protected override void OnResume() => Tick();

    // ---------- rain ----------

    private (double Y, double Speed, int Length)[] _drops;
    private const string RainChars = ":.;|'I#<>*+";

    private void Rain()
    {
        _drops ??= Enumerable.Range(0, Cols).Select(_ => (-_random.NextDouble() * Rows * 2, 0.25 + _random.NextDouble() * 0.6, 4 + _random.Next(10))).ToArray();
        Ascii.Clear(_dim);
        Ascii.Clear(_bright);
        for (int c = 0; c < Cols; c++)
        {
            if (c % 2 == 1) continue; // every other column, like the reference: airy instead of a wall of text
            var (y, speed, length) = _drops[c];
            y += speed;
            if (y - length > Rows) { y = -_random.Next(Rows); speed = 0.25 + _random.NextDouble() * 0.6; length = 4 + _random.Next(10); }
            _drops[c] = (y, speed, length);
            int head = (int)y;
            for (int k = 1; k <= length; k++)
            {
                int r = head - k;
                if (r >= 0 && r < Rows) _dim[r, c] = RainChars[(r * 7 + c * 3 + _frame / 3) % RainChars.Length];
            }
            if (head >= 0 && head < Rows) _bright[head, c] = RainChars[_random.Next(RainChars.Length)];
        }
    }

    private void Burst()
    {
        if (Mode == 4 && _drops != null)
            for (int c = 0; c < Cols; c++) _drops[c] = (-_random.Next(3), 0.7 + _random.NextDouble() * 0.6, 6 + _random.Next(10));
        else if (Mode == 2) _life = null;
        else if (Mode == 3) _spin += 1.2;
    }

    // ---------- life ----------

    private bool[,] _life;
    private int _stale, _lastCount;

    private void Life()
    {
        if (_life == null)
        {
            _life = new bool[Rows, Cols];
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++) _life[r, c] = _random.NextDouble() < 0.28;
            _stale = 0;
        }
        var next = new bool[Rows, Cols];
        int count = 0;
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
            {
                int n = 0;
                for (int dr = -1; dr <= 1; dr++)
                    for (int dc = -1; dc <= 1; dc++)
                        if ((dr != 0 || dc != 0) && _life[(r + dr + Rows) % Rows, (c + dc + Cols) % Cols]) n++;
                bool alive = _life[r, c] ? n is 2 or 3 : n == 3;
                next[r, c] = alive;
                if (alive) count++;
                _bright[r, c] = alive ? '#' : ' ';
                _dim[r, c] = !alive && _life[r, c] ? '.' : ' '; // the cells that just died fade out
            }
        _stale = Math.Abs(count - _lastCount) <= 2 ? _stale + 1 : 0;
        _lastCount = count;
        _life = next;
        if (_stale > 40 || count < Rows * Cols / 40) _life = null; // settled: seed a new world
    }

    // ---------- cava ----------

    private readonly double[] _bars = new double[Cols];
    private const string Blocks = " ▁▂▃▄▅▆▇█";

    private void Cava()
    {
        var media = MediaService.Instance;
        double level = media.IsPlaying ? Math.Sqrt(Volume.Peak()) : 0;
        double t = _frame * 0.18;
        Ascii.Clear(_dim);
        Ascii.Clear(_bright);
        for (int c = 0; c < Cols; c += 2)
        {
            // One level for everything, shaped into a moving spectrum: low notes left are a little stronger.
            double shape = 0.55 + 0.45 * Math.Sin(c * 0.37 + t) * Math.Cos(c * 0.11 - t * 0.7);
            double tilt = 1.15 - c / (double)Cols * 0.5;
            double target = Math.Clamp(level * shape * tilt + _random.NextDouble() * 0.06 * level, 0, 1);
            _bars[c] = target > _bars[c] ? target : _bars[c] * 0.82;
            double height = _bars[c] * Rows;
            for (int r = 0; r < Rows; r++)
            {
                double fill = height - (Rows - 1 - r);
                char ch = fill >= 1 ? '█' : fill > 0 ? Blocks[(int)(fill * 8)] : ' ';
                if (ch != ' ') (r < Rows - height + 1.5 ? _bright : _dim)[r, c] = ch;
                if (fill <= 0 && r == Rows - 1) _dim[r, c] = '_';
            }
        }
    }

    // ---------- orb ----------

    private double _spin;
    private const string Shades = ".,:-=+*#%@";

    private void Orb()
    {
        _spin += 0.07;
        double lx = Math.Cos(_spin), lz = Math.Sin(_spin), ly = -0.45;
        double norm = Math.Sqrt(lx * lx + ly * ly + lz * lz);
        lx /= norm; ly /= norm; lz /= norm;
        double radius = Rows / 2.0 - 0.5;
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
            {
                double x = (c - Cols / 2.0 + 0.5) / 2.0 / radius, y = (r - Rows / 2.0 + 0.5) / radius; // characters are about twice as tall as wide
                double d = x * x + y * y;
                _dim[r, c] = _bright[r, c] = ' ';
                if (d > 1)
                {
                    // A faint ring around the orb.
                    if (d < 1.12 && (c + r + _frame / 4) % 3 == 0) _dim[r, c] = '.';
                    continue;
                }
                double z = Math.Sqrt(1 - d);
                double light = Math.Max(0, x * lx + y * ly + z * lz);
                // Bands of latitude turning with the light, so the sphere visibly spins.
                double band = Math.Sin(Math.Atan2(x, z) * 6 + _spin * 3) * 0.12;
                int shade = (int)Math.Clamp((light + band) * (Shades.Length - 1), 0, Shades.Length - 1);
                (shade > 6 ? _bright : _dim)[r, c] = Shades[shade];
            }
    }

    // ---------- fetch ----------

    private static readonly string[] Logo =
    {
        "  ___  _ __  _   ___  __",
        " / _ \\| '_ \\| | | \\ \\/ /",
        "| (_) | | | | |_| |>  < ",
        " \\___/|_| |_|\\__, /_/\\_\\",
        "              |___/      ",
    };

    private void Fetch()
    {
        Ascii.Clear(_dim);
        Ascii.Clear(_bright);
        var sys = SystemMonitor.Instance;
        void Put(char[,] grid, int row, int col, string text)
        {
            for (int i = 0; i < text.Length && col + i < Cols; i++) grid[row, col + i] = text[i];
        }
        for (int i = 0; i < Logo.Length; i++) Put(_bright, i + 1, 1, Logo[i]);
        var lines = new (string Key, string Value)[]
        {
            ("os", "Windows 11"), ("shell", "onyx " + typeof(App).Assembly.GetName().Version?.ToString(3)), ("up", Ascii.Uptime()),
            ("cpu", $"{sys.Cpu:0} %" + (sys.CpuTemp is { } ct ? $"  {ct:0}°" : "")), ("gpu", sys.GpuTemp is { } gt ? $"{gt:0}°" : "-"),
            ("ram", $"{sys.RamUsedGB:0.0}/{sys.RamTotalGB:0} GB"), ("res", $"{SystemParameters.PrimaryScreenWidth:0}x{SystemParameters.PrimaryScreenHeight:0}"),
            ("theme", "mono / dot grid"),
        };
        Put(_bright, 7, 1, $"{Environment.UserName.ToLowerInvariant()}@onyx");
        Put(_dim, 8, 1, new string('-', Environment.UserName.Length + 5));
        for (int i = 0; i < lines.Length && 9 + i < Rows; i++)
        {
            Put(_dim, 9 + i, 1, lines[i].Key.PadRight(7));
            Put(_bright, 9 + i, 8, lines[i].Value);
        }
        // Color strip like neofetch, in shades of the mono palette.
        string strip = "███ ▓▓▓ ▒▒▒ ░░░";
        Put(_bright, 3, 30, strip[..3]);
        Put(_dim, 3, 34, strip[4..]);
        Put(_dim, 5, 30, ".:" + new string('=', 14));
        Put(_bright, 7, 30, $"cpu {Ascii.Gauge(sys.Cpu / 100, 10)}");
        Put(_bright, 8, 30, $"ram {Ascii.Gauge(sys.RamTotalGB > 0 ? sys.RamUsedGB / sys.RamTotalGB : 0, 10)}");
    }

    protected override Point DefaultPlace(Rect w) => new(w.Left + 16, w.Top + 306);
}
