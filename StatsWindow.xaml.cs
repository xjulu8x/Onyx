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
        RamFill.Background = new SolidColorBrush(c);
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
        RamFill.Width = _sys.RamTotalGB > 0 ? RamTrack.ActualWidth * _sys.RamUsedGB / _sys.RamTotalGB : 0;
        DrawCpu();
        DrawDisks();
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
            g.Children.Add(new TextBlock { Text = d.Name, FontSize = 13, FontWeight = FontWeights.Normal });
            g.Children.Add(new TextBlock
            {
                Text = $"{d.FreeGB:0.0} GB frei / {d.TotalGB:0.0} GB", FontSize = 12, FontWeight = FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Right,
            });
            g.Children.Add(new ProgressBar
            {
                Style = (Style)FindResource("Bar"), Height = 3, Value = d.UsedFraction, Foreground = Foreground,
                Margin = new Thickness(0, 21, 0, 0), VerticalAlignment = VerticalAlignment.Top,
            });
            DiskList.Children.Add(g);
        }
    }
}
