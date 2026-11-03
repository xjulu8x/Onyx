using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DesktopOverlay;

/// <summary>
/// Rounded slider (drag, click or mouse wheel). By default it controls the master volume and keeps itself in sync
/// with it; the second constructor makes it control any 0..1 value (e.g. the taskbar transparency).
/// </summary>
public class VolumeSlider : Grid
{
    private readonly Border _fill;
    private readonly Ellipse _thumb;
    private bool _dragging;
    private readonly Func<float> _get;
    private readonly Action<float> _set;
    private readonly Func<bool> _muted;

    /// <summary>Raised after the value or mute state changed (from here or elsewhere).</summary>
    public event Action<float, bool> Changed;

    public VolumeSlider() : this(Volume.Get, Volume.Set, Volume.IsMuted, syncWithSystem: true) { }

    public VolumeSlider(Func<float> get, Action<float> set, Func<bool> muted = null, bool syncWithSystem = false)
    {
        _get = get;
        _set = set;
        _muted = muted ?? (() => false);
        Height = 22;
        Background = Brushes.Transparent; // hit-testable across the full height
        Cursor = Cursors.Hand;
        Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255)) });
        _fill = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
        Children.Add(_fill);
        _thumb = new Ellipse { Width = 14, Height = 14, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
        Children.Add(_thumb);

        MouseLeftButtonDown += (_, e) => { _dragging = true; CaptureMouse(); SetFrom(e); };
        MouseMove += (_, e) => { if (_dragging) SetFrom(e); };
        MouseLeftButtonUp += (_, _) => { _dragging = false; ReleaseMouseCapture(); };
        MouseWheel += (_, e) => { _set(Math.Clamp(_get() + (e.Delta > 0 ? 0.02f : -0.02f), 0f, 1f)); Refresh(); };
        SizeChanged += (_, _) => Refresh();

        if (!syncWithSystem) return;
        // Sync with outside volume changes only while visible, so hidden sliders cost nothing and can be collected.
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        t.Tick += (_, _) => { if (!_dragging) Refresh(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) t.Start(); else t.Stop(); };
    }

    private void SetFrom(MouseEventArgs e)
    {
        if (ActualWidth <= 0) return;
        _set((float)Math.Clamp(e.GetPosition(this).X / ActualWidth, 0, 1));
        Refresh();
    }

    public void Refresh()
    {
        float v = _get();
        bool muted = _muted();
        double w = Math.Max(0, ActualWidth * v);
        _fill.Width = w;
        _fill.Opacity = _thumb.Opacity = muted ? 0.4 : 1;
        _thumb.Margin = new Thickness(Math.Clamp(w - 7, 0, Math.Max(0, ActualWidth - 14)), 0, 0, 0);
        Changed?.Invoke(v, muted);
    }

    public static string Glyph(float v, bool muted) => muted ? "" : v < 0.33 ? "" : v < 0.66 ? "" : "";
}
