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
            Left = SystemParameters.PrimaryScreenWidth - ActualWidth - 34;
            Top = SystemParameters.WorkArea.Bottom - ActualHeight - 12;
        };
        var s = SystemMonitor.Instance;
        Vendor.Text = s.CpuVendor;
        Model.Text = s.CpuModel;
        s.Updated += () =>
        {
            TempText.Text = s.CpuTemp is { } t ? $"{t:0}°" : "--°";
            SubText.Text = s.CpuTemp == null ? (SystemMonitor.CpuTempProblem is { } p ? p + " · klicken" : "") : "";
        };
        SubText.MouseLeftButtonUp += (_, _) => { if (s.CpuTemp == null) ((App)Application.Current).FixCpuTemp(); };
    }
}
