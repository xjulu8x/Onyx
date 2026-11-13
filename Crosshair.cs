using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopOverlay;

/// <summary>
/// Crosshair overlay in the middle of the main screen, like Crosshair X: always on top, never takes clicks or focus.
/// It shows over games in borderless or windowed mode; exclusive full screen draws past every overlay.
/// </summary>
public sealed class CrosshairWindow : Window
{
    public static readonly string[] Styles = { "Kreuz", "Kreuz + Punkt", "Punkt", "Kreis", "T" };
    public static readonly string[] Colors = { "#00FF66", "#FF453A", "#00E5FF", "#FFFF00", "#FFFFFF" };
    public static readonly string[] ColorNames = { "Grün", "Rot", "Cyan", "Gelb", "Weiß" };
    private const double Box = 120;
    private readonly DispatcherTimer _keepOnTop = new() { Interval = TimeSpan.FromSeconds(2) };

    public CrosshairWindow()
    {
        Width = Height = Box;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        Title = "Onyx-Crosshair";
        Content = new Mark();
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            // Click-through: transparent for the mouse, never activated, not in Alt+Tab.
            Native.SetWindowLong(h, Native.GWL_EXSTYLE, Native.GetWindowLong(h, Native.GWL_EXSTYLE) | 0x20 | 0x80000 | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
        };
        // Games raise themselves now and then; put the crosshair back on top.
        _keepOnTop.Tick += (_, _) => Native.BringTopmost(this);
        IsVisibleChanged += (_, _) => _keepOnTop.IsEnabled = IsVisible;
    }

    /// <summary>Exactly in the middle of the main screen, also after the resolution changed.</summary>
    public void Center()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Box) / 2;
        Top = (SystemParameters.PrimaryScreenHeight - Box) / 2;
    }

    public void Apply()
    {
        if (Settings.Current.CrosshairOn)
        {
            Center();
            Show();
            ((Mark)Content).InvalidateVisual();
            Native.BringTopmost(this);
        }
        else Hide();
    }

    private sealed class Mark : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            var s = Settings.Current;
            var color = Settings.ToColor(s.CrosshairColor, (byte)(255 * Math.Clamp(s.CrosshairOpacity, 0.2, 1)), System.Windows.Media.Colors.Lime);
            var fill = new SolidColorBrush(color);
            var outline = s.CrosshairOutline ? new Pen(new SolidColorBrush(Color.FromArgb(color.A, 0, 0, 0)), 1) : null;
            // Device pixels, so a 1 px line is really 1 px and sits on the exact centre.
            double k = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double c = Math.Round(Box / 2 * k) / k, t = Math.Max(1, s.CrosshairThickness) / k, len = s.CrosshairSize / k, gap = s.CrosshairGap / k;
            void Bar(double x, double y, double w, double h) => dc.DrawRectangle(fill, outline, new Rect(x, y, w, h));
            double half = t / 2;
            switch (s.CrosshairStyle)
            {
                case 2: Bar(c - half - t / 2, c - half - t / 2, t * 2, t * 2); break;
                case 3:
                    dc.DrawEllipse(null, new Pen(fill, t), new Point(c, c), len, len);
                    Bar(c - half, c - half, t, t);
                    break;
                default:
                    bool t4 = s.CrosshairStyle == 4;
                    if (!t4) Bar(c - half, c - gap - len, t, len); // up (missing in the T)
                    Bar(c - half, c + gap, t, len);                  // down
                    Bar(c - gap - len, c - half, len, t);            // left
                    Bar(c + gap, c - half, len, t);                  // right
                    if (s.CrosshairStyle == 1) Bar(c - half, c - half, t, t);
                    break;
            }
        }
    }
}
