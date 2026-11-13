using System.Windows;

namespace DesktopOverlay;

public partial class TempWindow : Window
{
    public TempWindow()
    {
        InitializeComponent();
        Native.MakeOverlay(this, desktopLevel: true);
        WidgetMover.Attach(this, GetType().Name);
        Loaded += (_, _) =>
        {
            // Under the system widget, so the cards of 2.3 have the lower right corner.
            Left = SystemParameters.WorkArea.Right - ActualWidth - 16;
            Top = SystemParameters.WorkArea.Top + 400;
        };
        var s = SystemMonitor.Instance;
        Vendor.Text = (s.CpuVendor ?? "").ToUpperInvariant();
        Model.Text = (s.CpuModel ?? "").ToUpperInvariant();
        s.Updated += () =>
        {
            TempText.Text = s.CpuTemp is { } t ? $"{t:0}°" : "--°";
            Gauge.Text = Bar(s.CpuTemp / 100);
            SubText.Visibility = s.CpuTemp == null ? Visibility.Visible : Visibility.Collapsed;
            SubText.Text = s.CpuTemp == null ? (SystemMonitor.CpuTempProblem is { } p ? p + " · klicken" : "") : "";
        };
        SubText.MouseLeftButtonUp += (_, _) => { if (s.CpuTemp == null) ((App)Application.Current).FixCpuTemp(); };
    }

    /// <summary>A text gauge in the terminal look: "[||||||....] 61%".</summary>
    public static string Bar(double? share, int cells = 10)
    {
        if (share is not { } v) return "[" + new string('.', cells) + "]";
        int full = (int)Math.Round(Math.Clamp(v, 0, 1) * cells);
        return "[" + new string('|', full) + new string('.', cells - full) + "]";
    }
}
