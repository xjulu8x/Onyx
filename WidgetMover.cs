using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace DesktopOverlay;

/// <summary>
/// Arrange mode for the desktop widgets, switched with the tab next to the open island (or in the settings).
/// While it is on, the widgets come to the front with a dashed frame and an × to remove them, and can be dragged.
/// Leaving the mode stores every position; removed widgets come back under Einstellungen → Widgets.
/// The drag starts only after the mouse has moved a few pixels, so plain clicks keep working.
/// </summary>
internal static class WidgetMover
{
    private const double DragThreshold = 4;
    private static readonly List<(Window Window, string Key)> Widgets = new();

    public static bool Editing { get; private set; }

    /// <summary>Raised when the mode changes, so the island tab and the settings can show it.</summary>
    public static event Action Changed;

    /// <summary>Called with the widget the user removed; the app hides it and remembers that.</summary>
    public static Action<Window> Remove;

    /// <summary>Called while the corner grip is dragged, with the widget's new own size (1 = normal).</summary>
    public static Action<Window, double> Resize;
    public static Func<Window, double> OwnScale;

    public static void SetEditing(bool on)
    {
        if (on == Editing) return;
        Editing = on;
        ShowGrid(on);
        foreach (var (w, key) in Widgets)
        {
            w.Cursor = on ? Cursors.SizeAll : null;
            var handle = new WindowInteropHelper(w).Handle;
            if (on)
            {
                Frame(w, true);
                if (handle != IntPtr.Zero) Native.SetWindowPos(handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            else
            {
                Frame(w, false);
                Remember(w, key); // store where everything ended up
                if (handle != IntPtr.Zero)
                {
                    Native.SetWindowPos(handle, Native.HWND_NOTOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                    Native.SetWindowPos(handle, Native.HWND_BOTTOM, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                }
            }
        }
        if (!on) Settings.Current.Save();
        Changed?.Invoke();
    }

    private static Window _grid;

    /// <summary>A faint dot grid over the screen while arranging, the same 16 pixels the widgets snap to. It lets every click through.</summary>
    private static void ShowGrid(bool on)
    {
        if (!on) { _grid?.Hide(); return; }
        if (_grid == null)
        {
            var area = SystemParameters.WorkArea;
            var dot = new DrawingBrush(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)), null, new RectangleGeometry(new Rect(0, 0, 1.5, 1.5))))
            {
                TileMode = TileMode.Tile, Viewport = new Rect(0, 0, Grid, Grid), ViewportUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, Grid, Grid), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None,
            };
            _grid = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, ShowInTaskbar = false, Topmost = true, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
                Left = area.Left, Top = area.Top, Width = area.Width, Height = area.Height, Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)),
                Content = new System.Windows.Controls.Border { Background = dot, Margin = new Thickness(Gap, Gap, 0, 0) },
            };
            _grid.SourceInitialized += (_, _) =>
            {
                var h = new WindowInteropHelper(_grid).Handle;
                Native.SetWindowLong(h, Native.GWL_EXSTYLE, Native.GetWindowLong(h, Native.GWL_EXSTYLE) | 0x20 /* click-through */ | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            };
        }
        _grid.Show();
    }

    private static void Frame(Window w, bool on)
    {
        if (w.Content is not UIElement content || AdornerLayer.GetAdornerLayer(content) is not { } layer) return;
        foreach (var old in layer.GetAdorners(content)?.OfType<EditFrame>() ?? Enumerable.Empty<EditFrame>()) layer.Remove(old);
        if (on) layer.Add(new EditFrame(content, w));
    }

    public static void Detach(Window w) => Widgets.RemoveAll(x => x.Window == w);

    private static void Remember(Window w, string key)
    {
        if (!double.IsNaN(w.Left) && !double.IsNaN(w.Top)) Settings.Current.WidgetPositions[key] = new[] { Math.Round(w.Left), Math.Round(w.Top) };
    }

    public static void Attach(Window w, string key)
    {
        Widgets.Add((w, key));
        // Every move while arranging is recorded (not only the end of a drag); it is written at the end of the drag and on leaving.
        w.LocationChanged += (_, _) => { if (Editing) Remember(w, key); };
        w.IsVisibleChanged += (_, _) => { if (Editing && w.IsVisible) w.Dispatcher.BeginInvoke(() => Frame(w, true)); };
        // Deferred so it runs after the widget's own Loaded handler that sets the default position.
        w.Loaded += (_, _) => w.Dispatcher.BeginInvoke(() =>
        {
            if (Settings.Current.WidgetPositions.TryGetValue(key, out var p) && p.Length == 2 && OnScreen(p[0], p[1]))
            {
                w.Left = p[0];
                w.Top = p[1];
            }
        });

        // Own drag instead of DragMove: the widget follows the mouse and snaps to the grid and to its neighbours as it moves.
        Point? down = null;
        Point grab = default;
        bool dragging = false;
        w.PreviewMouseLeftButtonDown += (_, e) => down = Editing && e.OriginalSource is not FrameworkElement { Tag: "remove" } ? e.GetPosition(w) : null;
        w.PreviewMouseMove += (_, e) =>
        {
            if (down is not { } start || e.LeftButton != MouseButtonState.Pressed) { down = null; return; }
            if (!dragging)
            {
                var v = e.GetPosition(w) - start;
                if (Math.Abs(v.X) < DragThreshold && Math.Abs(v.Y) < DragThreshold) return;
                dragging = true;
                grab = start;
                w.CaptureMouse();
            }
            var mouse = w.PointToScreen(e.GetPosition(w));
            var dpi = VisualTreeHelper.GetDpi(w);
            var (x, y) = Snap(w, mouse.X / dpi.DpiScaleX - grab.X, mouse.Y / dpi.DpiScaleY - grab.Y);
            w.Left = x;
            w.Top = y;
        };
        w.PreviewMouseLeftButtonUp += (_, e) =>
        {
            down = null;
            if (dragging)
            {
                dragging = false;
                w.ReleaseMouseCapture();
                Remember(w, key);
                Settings.Current.Save();
            }
            // While arranging, clicks inside a widget only move it; its own keys stay quiet.
            if (Editing && e.OriginalSource is not FrameworkElement { Tag: "remove" }) e.Handled = true;
        };
    }

    private const double Grid = 16, Magnet = 14, Gap = 12;

    /// <summary>
    /// Snapping: edges of other widgets and of the screen pull within a few pixels (with a small gap between widgets,
    /// or flush with their edges); otherwise the position falls on a 16-pixel grid.
    /// </summary>
    private static (double X, double Y) Snap(Window w, double x, double y)
    {
        double width = w.ActualWidth, height = w.ActualHeight;
        var area = SystemParameters.WorkArea;
        var xs = new List<double> { area.Left + Gap, area.Right - Gap - width };
        var ys = new List<double> { area.Top + Gap, area.Bottom - Gap - height };
        foreach (var (other, _) in Widgets)
        {
            if (other == w || !other.IsVisible) continue;
            double l = other.Left, t = other.Top, r = l + other.ActualWidth, b = t + other.ActualHeight;
            xs.AddRange(new[] { l, r - width, r + Gap, l - Gap - width });
            ys.AddRange(new[] { t, b - height, b + Gap, t - Gap - height });
        }
        double Pick(double value, List<double> lines, double origin)
        {
            var near = lines.OrderBy(l => Math.Abs(l - value)).First();
            return Math.Abs(near - value) <= Magnet ? near : origin + Math.Round((value - origin) / Grid) * Grid;
        }
        return (Pick(x, xs, area.Left), Pick(y, ys, area.Top));
    }

    private static bool OnScreen(double left, double top) =>
        left > SystemParameters.VirtualScreenLeft - 50 && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50
        && top > SystemParameters.VirtualScreenTop - 20 && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 20;

    /// <summary>Dashed outline and a small × in the corner, drawn over a widget while arranging.</summary>
    private sealed class EditFrame : Adorner
    {
        private static readonly Pen Dash = MakeDash();
        private readonly Border _remove, _grip;
        private readonly VisualCollection _children;

        private static Pen MakeDash()
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)), 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
            pen.Freeze();
            return pen;
        }

        public EditFrame(UIElement target, Window w) : base(target)
        {
            Action remove = () => Remove?.Invoke(w);
            _remove = new Border
            {
                Width = 20, Height = 20, Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)), CornerRadius = new CornerRadius(2), Cursor = Cursors.Hand,
                Tag = "remove", ToolTip = "Widget entfernen",
                Child = new TextBlock { Text = "×", Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0), Tag = "remove" },
            };
            _remove.MouseLeftButtonUp += (_, e) => { remove(); e.Handled = true; };
            // Corner grip: drag to make the widget bigger or smaller; everything in it scales along.
            _grip = new Border
            {
                Width = 16, Height = 16, Background = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)), CornerRadius = new CornerRadius(2), Cursor = Cursors.SizeNWSE,
                Tag = "remove", ToolTip = "Größe ziehen",
                Child = new TextBlock { Text = "\u25E2", Foreground = Brushes.Black, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Tag = "remove" },
            };
            Point start = default;
            double startScale = 1, startWidth = 1;
            _grip.MouseLeftButtonDown += (_, e) =>
            {
                start = e.GetPosition(w);
                startScale = OwnScale?.Invoke(w) ?? 1;
                startWidth = Math.Max(1, ((FrameworkElement)w.Content).ActualWidth);
                _grip.CaptureMouse();
                e.Handled = true;
            };
            _grip.MouseMove += (_, e) =>
            {
                if (!_grip.IsMouseCaptured) return;
                var at = e.GetPosition(w);
                // The window grows to the right and down from its top left corner, so positions in it stay comparable.
                double grow = Math.Max(at.X - start.X, (at.Y - start.Y) * startWidth / Math.Max(1, ((FrameworkElement)w.Content).ActualHeight));
                Resize?.Invoke(w, Math.Clamp(startScale * (startWidth + grow) / startWidth, 0.5, 2));
            };
            _grip.MouseLeftButtonUp += (_, e) => { _grip.ReleaseMouseCapture(); Settings.Current.Save(); e.Handled = true; };
            _children = new VisualCollection(this) { _remove, _grip };
        }

        protected override int VisualChildrenCount => _children.Count;
        protected override Visual GetVisualChild(int index) => _children[index];

        protected override Size ArrangeOverride(Size finalSize)
        {
            _remove.Arrange(new Rect(finalSize.Width - 26, 6, 20, 20));
            _grip.Arrange(new Rect(finalSize.Width - 20, finalSize.Height - 20, 16, 16));
            return finalSize;
        }

        protected override Size MeasureOverride(Size constraint)
        {
            _remove.Measure(new Size(20, 20));
            _grip.Measure(new Size(16, 16));
            return base.MeasureOverride(constraint);
        }

        protected override void OnRender(DrawingContext dc)
        {
            var size = AdornedElement.RenderSize;
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255)), Dash, new Rect(0.5, 0.5, size.Width - 1, size.Height - 1));
        }
    }
}
