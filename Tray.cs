using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ManagedShell.Interop;
using ManagedShell.WindowsTray;

namespace DesktopOverlay;

/// <summary>
/// The notification-area icons of background apps (Steam, Discord, ...). The Windows taskbar that normally shows them is
/// hidden, so ManagedShell receives them instead; clicks are passed on to the owning app.
/// </summary>
internal static class Tray
{
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern bool ChangeWindowMessageFilterEx(IntPtr h, uint msg, uint action, IntPtr info);

    private static NotificationArea _area;

    /// <summary>Raised when icons appeared, disappeared or were hidden.</summary>
    public static event Action Changed;

    public static void Start()
    {
        try
        {
            _area = new NotificationArea(Array.Empty<string>(), new TrayService(), new ExplorerTrayService());
            _area.Initialize();
            _area.TrayIcons.CollectionChanged += OnIconsChanged;
            foreach (var icon in _area.TrayIcons) icon.PropertyChanged += OnIconChanged;
            AcceptNormalApps();
        }
        catch { _area = null; }
    }

    /// <summary>
    /// Onyx runs as admin, and Windows blocks messages from normal programs to admin windows. Apps register their icons
    /// with WM_COPYDATA, so that one message is let through; then they are asked to register again.
    /// </summary>
    private static void AcceptNormalApps()
    {
        const uint WM_COPYDATA = 0x4A, MSGFLT_ALLOW = 1;
        ChangeWindowMessageFilterEx(_area.Handle, WM_COPYDATA, MSGFLT_ALLOW, IntPtr.Zero);
        Win32.PostMessage(new IntPtr(0xFFFF) /* HWND_BROADCAST */, Win32.RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero);
    }

    public static void Stop()
    {
        var area = _area;
        _area = null;
        try { area?.Dispose(); } catch { }
    }

    private static void OnIconsChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (NotifyIcon icon in e.NewItems ?? Array.Empty<NotifyIcon>()) icon.PropertyChanged += OnIconChanged;
        foreach (NotifyIcon icon in e.OldItems ?? Array.Empty<NotifyIcon>()) icon.PropertyChanged -= OnIconChanged;
        Changed?.Invoke();
    }

    private static void OnIconChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NotifyIcon.IsHidden)) Changed?.Invoke();
    }

    /// <summary>Icons to show. Network and volume are left out, the bar has its own buttons for those.</summary>
    public static List<NotifyIcon> Visible()
    {
        if (_area == null) return new();
        return _area.TrayIcons.Where(i => !i.IsHidden && i.Icon != null && !IsBuiltIn(i)).ToList();
    }

    private static bool IsBuiltIn(NotifyIcon icon)
    {
        var id = icon.GUID.ToString();
        return id.Equals(NotificationArea.NETWORK_GUID, StringComparison.OrdinalIgnoreCase)
            || id.Equals(NotificationArea.VOLUME_GUID, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One clickable icon. The app decides what a click does, usually opening its window or its own menu.</summary>
    public static FrameworkElement Button(NotifyIcon icon)
    {
        var image = new Image { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        image.SetBinding(Image.SourceProperty, new Binding(nameof(NotifyIcon.Icon)) { Source = icon });

        var hover = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255));
        var none = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0)); // hit-testable, but invisible
        var button = new Border { Width = 30, Height = 34, CornerRadius = new CornerRadius(8), Background = none, Cursor = Cursors.Hand, Child = image };
        button.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(NotifyIcon.Title)) { Source = icon });

        button.MouseEnter += (_, _) => { button.Background = hover; Place(icon, button); icon.IconMouseEnter(Mouse()); };
        button.MouseLeave += (_, _) => { button.Background = none; icon.IconMouseLeave(Mouse()); };
        button.MouseMove += (_, _) => icon.IconMouseMove(Mouse());
        button.MouseDown += (_, e) => { Place(icon, button); icon.IconMouseDown(e.ChangedButton, Mouse(), (int)GetDoubleClickTime()); e.Handled = true; };
        button.MouseUp += (_, e) => { icon.IconMouseUp(e.ChangedButton, Mouse(), (int)GetDoubleClickTime()); e.Handled = true; };
        return button;
    }

    /// <summary>Apps use this rectangle to place their own menus next to the icon.</summary>
    private static void Place(NotifyIcon icon, FrameworkElement button)
    {
        if (PresentationSource.FromVisual(button) == null) return;
        var topLeft = button.PointToScreen(new Point(0, 0));
        var bottomRight = button.PointToScreen(new Point(button.ActualWidth, button.ActualHeight));
        icon.Placement = new NativeMethods.Rect { Left = (int)topLeft.X, Top = (int)topLeft.Y, Right = (int)bottomRight.X, Bottom = (int)bottomRight.Y };
    }

    /// <summary>Cursor position packed the way tray callback messages expect it.</summary>
    private static uint Mouse()
    {
        Win32.GetCursorPos(out var p);
        return (uint)(((ushort)p.Y << 16) | (ushort)p.X);
    }
}
