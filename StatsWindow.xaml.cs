using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopOverlay;

public partial class StatsWindow : Window
{
    private readonly SystemMonitor _sys = SystemMonitor.Instance;

    public StatsWindow()
    {
        InitializeComponent();
        Native.MakeOverlay(this, desktopLevel: true);
        WidgetMover.Attach(this, GetType().Name);
        Loaded += (_, _) => Left = SystemParameters.PrimaryScreenWidth - Width - 16;
        _sys.Updated += Update;
        // The graphs follow the widget color chosen in the island settings.
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ForegroundProperty, typeof(Window)).AddValueChanged(this, (_, _) => RecolorGraphs());
        RecolorGraphs();
        Update();
    }

    private void RecolorGraphs()
    {
        var c = Foreground is SolidColorBrush b ? b.Color : Colors.White;
        // Flat look: just the CPU line (no shaded area below it) and a solid RAM bar.
        CpuFill.Fill = null;
        RamFill.Background = CpuBarFill.Background = new SolidColorBrush(c);
        foreach (var bar in DiskList.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<ProgressBar>())) bar.Foreground = Foreground;
    }

    private void Update()
    {
        DownVal.Text = SystemMonitor.Rate(_sys.DownBps);
        UpVal.Text = SystemMonitor.Rate(_sys.UpBps);
        PingVal.Text = _sys.PingMs is { } p ? $"{p:0.0} ms" : "-";
        GpuTemp.Text = $"{_sys.GpuTemp ?? 0:0}°";
        CpuVal.Text = $"{_sys.Cpu:0.0} %";
        RamVal.Text = $"{_sys.RamUsedGB:0.0} GB/{_sys.RamTotalGB:0.0} GB";
        double ram = _sys.RamTotalGB > 0 ? _sys.RamUsedGB / _sys.RamTotalGB : 0, cpu = _sys.Cpu / 100;
        int style = Settings.Current.WidgetGraph; // 0 line, 1 bars, 2 rings
        CpuCanvas.Visibility = style == 0 ? Visibility.Visible : Visibility.Collapsed;
        CpuTrack.Visibility = style == 1 ? Visibility.Visible : Visibility.Collapsed;
        CpuRingBox.Visibility = RamRingBox.Visibility = style == 2 ? Visibility.Visible : Visibility.Collapsed;
        RamTrack.Visibility = style == 2 ? Visibility.Collapsed : Visibility.Visible;
        RamFill.Width = RamTrack.ActualWidth * ram;
        if (style == 0) DrawCpu();
        if (style == 1) CpuBarFill.Width = CpuTrack.ActualWidth * cpu;
        if (style == 2)
        {
            CpuRing.Data = Arc(cpu, 64);
            RamRing.Data = Arc(ram, 64);
        }
        DrawDisks();
    }

    /// <summary>Clockwise arc from the top of a ring, for the given share of the circle.</summary>
    private static Geometry Arc(double share, double size)
    {
        double r = size / 2 - 3, c = size / 2, angle = Math.Clamp(share, 0.001, 0.999) * 2 * Math.PI;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(c, c - r), false, false);
            g.ArcTo(new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle)), new Size(r, r), 0, angle > Math.PI, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    // ---------- single widgets ----------

    private readonly List<Window> _parts = new();
    private static readonly Dictionary<string, string> Names = new() { ["Net"] = "NETZ", ["Gpu"] = "GPU", ["Cpu"] = "CPU", ["Ram"] = "RAM", ["Disks"] = "DISKS" };

    /// <summary>The separate widgets while "Einzeln" is on; otherwise empty.</summary>
    public IReadOnlyList<Window> Parts => _parts;

    /// <summary>Moves the blocks into their own small windows, which can be placed one by one, or back into this one.</summary>
    public void ApplyLayout()
    {
        bool free = Settings.Current.WidgetsFree;
        if (free == _parts.Count > 0) return;
        var blocks = new (FrameworkElement Block, string Key, Thickness Margin)[]
        {
            (Net, "Net", new Thickness(0)), (GpuBlock, "Gpu", new Thickness(0, 18, 0, 14)), (CpuBlock, "Cpu", new Thickness(0)),
            (RamBlock, "Ram", new Thickness(0, 14, 0, 0)), (DiskList, "Disks", new Thickness(0, 14, 0, 0)),
        };
        if (!free)
        {
            foreach (var part in _parts)
            {
                WidgetMover.Detach(part);
                ((DockPanel)((Border)part.Content).Child).Children.RemoveAt(1);
                part.Close();
            }
            _parts.Clear();
            foreach (var (block, _, margin) in blocks)
            {
                block.Margin = margin;
                Blocks.Children.Add(block);
            }
            return;
        }

        Hide();
        double width = 250 * Settings.Current.WidgetScale, left = SystemParameters.PrimaryScreenWidth - width - 16, y = 14;
        foreach (var (block, key, _) in blocks)
        {
            Blocks.Children.Remove(block);
            block.Margin = new Thickness(0);
            // Every part is a framed widget of its own, with a caption like "+ CPU / 03".
            var body = new DockPanel();
            var caption = Theme.Tag($"+ {Names[key]} / {_parts.Count + 1:00}");
            caption.Margin = new Thickness(0, 0, 0, 10);
            DockPanel.SetDock(caption, Dock.Top);
            body.Children.Add(caption);
            body.Children.Add(block);
            var part = new Window
            {
                Title = "Stats-" + key, Tag = "wide", Width = width, SizeToContent = SizeToContent.Height, Left = left, Top = y,
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize,
                Foreground = Foreground,
                Content = new Border { Background = Frame.Background, BorderBrush = Frame.BorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), Padding = new Thickness(14, 10, 14, 12), Child = body },
            };
            part.SetResourceReference(FontFamilyProperty, "Ui");
            TextOptions.SetTextRenderingMode(part, TextRenderingMode.Grayscale);
            Native.MakeOverlay(part, desktopLevel: true);
            WidgetMover.Attach(part, part.Title);
            _parts.Add(part);
            part.Show();
            part.UpdateLayout();
            y += part.ActualHeight + 8;
        }
    }

    private void DrawCpu()
    {
        var h = _sys.CpuHistory;
        double w = CpuCanvas.ActualWidth, hh = CpuCanvas.ActualHeight;
        if (h.Count < 2 || w <= 0) return;
        const int slots = 90;
        double max = Math.Max(10, h.Max() * 1.25);
        double step = w / (slots - 1);
        double x0 = w - (h.Count - 1) * step;

        var line = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var lc = line.Open())
        using (var fc = fill.Open())
        {
            Point P(int i) => new(x0 + i * step, hh - 2 - (hh - 4) * h[i] / max);
            lc.BeginFigure(P(0), false, false);
            fc.BeginFigure(new Point(x0, hh), true, true);
            fc.LineTo(P(0), false, false);
            for (int i = 1; i < h.Count; i++) { lc.LineTo(P(i), true, true); fc.LineTo(P(i), false, false); }
            fc.LineTo(new Point(w, hh), false, false);
        }
        line.Freeze(); fill.Freeze();
        CpuLine.Data = line;
        CpuFill.Data = fill;
    }

    private void DrawDisks()
    {
        DiskList.Children.Clear();
        foreach (var d in _sys.Disks)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            g.Children.Add(new TextBlock { Text = d.Name.ToUpperInvariant(), FontFamily = Theme.Mono, FontSize = 10, Opacity = 0.7 });
            g.Children.Add(new TextBlock
            {
                Text = $"{d.FreeGB:0} / {d.TotalGB:0} GB FREI", FontFamily = Theme.Mono, FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Right,
            });
            g.Children.Add(new ProgressBar
            {
                Style = (Style)FindResource("Bar"), Height = 2, Value = d.UsedFraction, Foreground = Foreground,
                Margin = new Thickness(0, 18, 0, 0), VerticalAlignment = VerticalAlignment.Top,
            });
            DiskList.Children.Add(g);
        }
    }
}
