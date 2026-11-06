using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopOverlay;

/// <summary>
/// Arrange mode for the desktop widgets, started with "Widgets anordnen" in the island settings and ended with "Fertig".
/// While it is on, each widget shows a light frame and can be dragged; the position is saved and restored on start.
/// The drag starts only after the mouse has moved a few pixels, so plain clicks keep working.
/// </summary>
internal static class WidgetMover
{
    private const double DragThreshold = 4;
    private static readonly List<(Window Window, string Key)> Widgets = new();
    private static readonly Brush Frame = new SolidColorBrush(Color.FromArgb(0x1C, 255, 255, 255));

    public static bool Editing { get; private set; }

    public static void SetEditing(bool on)
    {
        Editing = on;
        foreach (var (w, key) in Widgets)
        {
            w.Background = on ? Frame : Brushes.Transparent; // also makes the empty areas grabbable
            w.Cursor = on ? Cursors.SizeAll : null;
            if (!on) Remember(w, key); // "Fertig": store where everything ended up
        }
        if (!on) Settings.Current.Save();
    }

    public static void Detach(Window w) => Widgets.RemoveAll(x => x.Window == w);

    private static void Remember(Window w, string key)
    {
        if (!double.IsNaN(w.Left) && !double.IsNaN(w.Top)) Settings.Current.WidgetPositions[key] = new[] { Math.Round(w.Left), Math.Round(w.Top) };
    }

    public static void Attach(Window w, string key)
    {
        Widgets.Add((w, key));
        // Every move while arranging is recorded (not only the end of a drag); it is written at the end of the drag and on "Fertig".
        w.LocationChanged += (_, _) => { if (Editing) Remember(w, key); };
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
        w.PreviewMouseLeftButtonDown += (_, e) => down = Editing ? e.GetPosition(w) : null;
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
    }

    private static bool OnScreen(double left, double top) =>
        left > SystemParameters.VirtualScreenLeft - 50 && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50
        && top > SystemParameters.VirtualScreenTop - 20 && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 20;
}
