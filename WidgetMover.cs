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

        Point? down = null;
        w.PreviewMouseLeftButtonDown += (_, e) => down = Editing && e.OriginalSource is not FrameworkElement { Tag: "remove" } ? e.GetPosition(w) : null;
        w.PreviewMouseLeftButtonUp += (_, _) => down = null;
        w.PreviewMouseMove += (_, e) =>
        {
            if (down is not { } start || e.LeftButton != MouseButtonState.Pressed) { down = null; return; }
            var v = e.GetPosition(w) - start;
            if (Math.Abs(v.X) < DragThreshold && Math.Abs(v.Y) < DragThreshold) return;
            down = null;
            w.DragMove();
            Remember(w, key);
            Settings.Current.Save();
        };
        // While arranging, clicks inside a widget only move it; its own keys stay quiet.
        w.PreviewMouseLeftButtonUp += (_, e) => { if (Editing && e.OriginalSource is not FrameworkElement { Tag: "remove" }) e.Handled = true; };
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
