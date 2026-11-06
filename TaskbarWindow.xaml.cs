using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DesktopOverlay;

/// <summary>Replacement taskbar: media chip, centered start + app icons, tray. Registered as an app bar so maximized windows leave room for it.</summary>
public partial class TaskbarWindow : Window
{
    private const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABE_BOTTOM = 3, ABN_FULLSCREENAPP = 2;
    private static readonly Brush HoverBrush = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255));
    private static readonly Brush NoBrush = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0));

    private readonly MediaService _media = MediaService.Instance;
    private IntPtr _hwnd;
    private uint _callbackMsg;
    private const double Gap = 8; // floating distance from the screen edges

    public static event Action<bool> FullscreenChanged;

    public TaskbarWindow()
    {
        InitializeComponent();
        Native.MakeOverlay(this, desktopLevel: false);
        SourceInitialized += (_, _) => RegisterAppBar();
        Closed += (_, _) => UnregisterAppBar();

        SourceInitialized += (_, _) => ApplyLook();
        _media.Updated += UpdateMedia;

        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        t.Tick += (_, _) => Refresh();
        t.Start();
        Refresh();
        HookWindowEvents();
        StartFullscreenWatch();
        UpdateMedia();
    }

    public void ApplyLook()
    {
        // Never fully transparent: at least 15 % of the bar's color stays, so icons remain readable.
        byte alpha = (byte)Math.Round(255 * Math.Clamp(1 - Settings.Current.Transparency, 0.15, 1));
        Bg.Background = new SolidColorBrush(Settings.ToColor(Settings.Current.TaskbarColor, alpha, Color.FromRgb(0x06, 0x06, 0x08)));

        var s = Settings.Current;
        Bg.CornerRadius = new CornerRadius(s.TaskbarRadius);
        StartBtn.Height = s.TaskbarHeight - 6;
        foreach (var r in StartLogo.Children.OfType<System.Windows.Shapes.Rectangle>()) r.Fill = Theme.AccentBrush;
        foreach (var btn in _buttons.Values)
        {
            btn.Root.Height = s.TaskbarHeight - 6;
            btn.Icon.Width = btn.Icon.Height = s.TaskbarIconSize;
            if (btn.Active) btn.Indicator.Background = Theme.AccentBrush;
        }
        // Apps on the left: the music chip moves to the middle so they don't overlap.
        CenterPanel.HorizontalAlignment = s.TaskbarAppsLeft ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        CenterPanel.Margin = new Thickness(s.TaskbarAppsLeft ? 6 : 0, 0, 0, 0);
        MediaChip.HorizontalAlignment = s.TaskbarAppsLeft ? HorizontalAlignment.Center : HorizontalAlignment.Left;

        if (Height != s.TaskbarHeight)
        {
            Height = s.TaskbarHeight;
            if (_hwnd != IntPtr.Zero) PositionAppBar();
        }
    }

    // ---------- notification area ----------

    private const int MaxTrayIcons = 8; // more go behind the arrow, so the bar stays tidy

    public void StartTray()
    {
        Tray.Changed += () => Dispatcher.BeginInvoke(RebuildTray);
        Tray.Start();
        RebuildTray();
    }

    private void RebuildTray()
    {
        TrayPanel.Children.Clear();
        var icons = Tray.Visible();
        foreach (var icon in icons.Take(MaxTrayIcons)) TrayPanel.Children.Add(Tray.Button(icon));
        if (icons.Count <= MaxTrayIcons) return;
        var more = new Button { Style = (Style)FindResource("TrayButton"), Content = "", FontSize = 11, ToolTip = "Weitere Symbole" };
        more.Click += (_, _) => Flyout.Toggle(FlyoutKind.Tray);
        TrayPanel.Children.Add(more);
    }

    // ---------- auto-hide ----------

    private DispatcherTimer _autoHideTimer;
    private DateTime? _hideAt;

    /// <summary>Switches auto-hide on/off: the bar stops reserving screen space and only appears at the bottom edge.</summary>
    public void ApplyAutoHide()
    {
        bool on = Settings.Current.TaskbarAutoHide;
        if (_hwnd != IntPtr.Zero)
        {
            var abd = NewAbd();
            if (on) Win32.SHAppBarMessage(ABM_REMOVE, ref abd);
            else { abd.uCallbackMessage = _callbackMsg; Win32.SHAppBarMessage(ABM_NEW, ref abd); }
            PositionAppBar();
        }
        _autoHideTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => AutoHideTick(), Dispatcher);
        if (on) _autoHideTimer.Start();
        else { _autoHideTimer.Stop(); _hideAt = null; if (!_fullscreen) ShowBar(); }
    }

    private void AutoHideTick()
    {
        if (_fullscreen || _peeking) return; // the fullscreen logic owns the bar then
        var app = (App)Application.Current;
        if (!Win32.GetCursorPos(out var p)) return;
        var src = PresentationSource.FromVisual(this);
        double scale = src?.CompositionTarget?.TransformToDevice.M22 ?? 1;
        double screenH = SystemParameters.PrimaryScreenHeight * scale, screenW = SystemParameters.PrimaryScreenWidth * scale;
        double barTop = screenH - (Height + Gap) * scale;

        bool onPrimary = p.X >= 0 && p.X < screenW;
        bool atEdge = onPrimary && p.Y >= screenH - 2;
        bool overBar = _barShown && onPrimary && p.Y >= barTop - 12;
        bool popupOpen = app.StartMenu.IsVisible || app.Flyout.IsVisible || MenuClickWatch.IsOpen;

        if (atEdge || overBar || app.StartMenu.IsVisible) { _hideAt = null; ShowBar(); }
        else if (_barShown && !popupOpen)
        {
            _hideAt ??= DateTime.Now.AddMilliseconds(450);
            if (DateTime.Now >= _hideAt) { _hideAt = null; HideBar(); }
        }
    }

    // ---------- app bar ----------

    private void RegisterAppBar()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _callbackMsg = Win32.RegisterWindowMessage("Onyx.AppBar");
        var abd = NewAbd();
        abd.uCallbackMessage = _callbackMsg;
        Win32.SHAppBarMessage(ABM_NEW, ref abd);
        PositionAppBar();
        HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
        if (Settings.Current.TaskbarAutoHide) ApplyAutoHide();
    }

    private void PositionAppBar()
    {
        var src = PresentationSource.FromVisual(this);
        double scale = src?.CompositionTarget?.TransformToDevice.M22 ?? 1;
        int screenW = (int)Math.Round(SystemParameters.PrimaryScreenWidth * scale);
        int screenH = (int)Math.Round(SystemParameters.PrimaryScreenHeight * scale);
        int h = (int)Math.Round(Height * scale), gap = (int)Math.Round(Gap * scale);

        if (Settings.Current.TaskbarAutoHide)
        {
            // No reserved strip: maximized windows use the full screen and the bar floats over them when shown.
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, gap, screenH - h - gap, screenW - 2 * gap, h, Native.SWP_NOACTIVATE);
            return;
        }
        var abd = NewAbd();
        abd.uEdge = ABE_BOTTOM;
        abd.rc = new Native.RECT { Left = 0, Right = screenW, Top = screenH - h - 2 * gap, Bottom = screenH };
        Win32.SHAppBarMessage(ABM_QUERYPOS, ref abd);
        abd.rc.Top = abd.rc.Bottom - h - 2 * gap;
        Win32.SHAppBarMessage(ABM_SETPOS, ref abd);
        // The reserved strip includes the gaps; the bar itself floats inside it.
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, abd.rc.Left + gap, abd.rc.Bottom - h - gap, abd.rc.Right - abd.rc.Left - 2 * gap, h, Native.SWP_NOACTIVATE);
    }

    private void UnregisterAppBar()
    {
        if (_hwnd == IntPtr.Zero) return;
        var abd = NewAbd();
        Win32.SHAppBarMessage(ABM_REMOVE, ref abd);
        _hwnd = IntPtr.Zero;
    }

    private Win32.APPBARDATA NewAbd() => new() { cbSize = Marshal.SizeOf<Win32.APPBARDATA>(), hWnd = _hwnd };

    private IntPtr WndProc(IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (msg == (int)_callbackMsg && (uint)wp == ABN_FULLSCREENAPP) CheckFullscreen();
        return IntPtr.Zero;
    }

    // ---------- fullscreen games ----------
    //
    // States: normal (bar visible) -> fullscreen (bar slides away) -> peeking (Win key: bar back until the game is clicked).

    private bool _fullscreen, _peeking, _peekLeftGame, _barShown = true;
    private readonly TranslateTransform _barSlide = new();
    private DispatcherTimer _fullscreenTimer;
    private IntPtr _mouseHook;
    private MouseHookProc _mouseProc; // kept alive for the GC

    private void StartFullscreenWatch()
    {
        Root.RenderTransform = _barSlide;
        ShowActivated = false;
        // Cheap check (foreground window + its rect), fast enough to catch games that resize into fullscreen after focusing.
        _fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _fullscreenTimer.Tick += (_, _) => CheckFullscreen();
        _fullscreenTimer.Start();
    }

    /// <summary>Called on every foreground change, poll and shell notice.</summary>
    private void CheckFullscreen()
    {
        var fg = Win32.GetForegroundWindow();
        if (IsOwnWindow(fg)) { if (_peeking) _peekLeftGame = true; return; }
        bool full = IsFullscreen(fg);

        if (_peeking)
        {
            if (full && _peekLeftGame) EndPeek(hide: true);        // back in the game (clicked it / closed Start)
            else if (!full) EndPeek(hide: false);                 // switched to a normal app: plain normal mode
            return;
        }
        SetFullscreen(full);
    }

    private void SetFullscreen(bool full)
    {
        if (full == _fullscreen) return;
        _fullscreen = full;
        if (full) HideBar(); else ShowBar();
        FullscreenChanged?.Invoke(full);
    }

    /// <summary>Win key during a fullscreen game: the bar is back immediately and stays until the game gets a click.</summary>
    public void Peek()
    {
        if (!_fullscreen || _peeking) return;
        _peeking = true;
        _peekLeftGame = false;
        ShowBar();
        InstallMouseHook();
    }

    private void EndPeek(bool hide)
    {
        _peeking = false;
        RemoveMouseHook();
        if (hide) HideBar();
        else { _fullscreen = false; FullscreenChanged?.Invoke(false); }
    }

    private void ShowBar()
    {
        if (_barShown && IsVisible) return;
        _barShown = true;
        Show();
        Topmost = true;
        Native.BringTopmost(this);
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Theme.Ms(160)) { EasingFunction = Smooth });
        _barSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24, 0, Theme.Ms(220)) { EasingFunction = Smooth });
    }

    private void HideBar()
    {
        if (!_barShown) return;
        _barShown = false;
        var fade = new DoubleAnimation(0, Theme.Ms(140));
        fade.Completed += (_, _) => { if (!_barShown) { Topmost = false; Hide(); } };
        Root.BeginAnimation(OpacityProperty, fade);
        _barSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24, Theme.Ms(140)));
    }

    // While peeking, a click anywhere outside our own windows (e.g. into the game) ends the peek.
    private delegate IntPtr MouseHookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, MouseHookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Native.POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return;
        _mouseProc = (code, wp, lp) =>
        {
            const int WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204;
            if (code >= 0 && (wp == WM_LBUTTONDOWN || wp == WM_RBUTTONDOWN))
            {
                var pt = Marshal.PtrToStructure<Native.POINT>(lp);
                var target = GetAncestor(WindowFromPoint(pt), 2 /* GA_ROOT */);
                if (!IsOwnWindow(target))
                    Dispatcher.BeginInvoke(async () =>
                    {
                        _peekLeftGame = true;
                        await Task.Delay(120); // let the click move focus first
                        CheckFullscreen();
                    });
            }
            return CallNextHookEx(_mouseHook, code, wp, lp);
        };
        using var mod = Process.GetCurrentProcess().MainModule!;
        _mouseHook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _mouseProc, GetModuleHandle(mod.ModuleName), 0);
    }

    private void RemoveMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
    }

    private static bool IsOwnWindow(IntPtr h)
    {
        if (h == IntPtr.Zero) return false;
        Win32.GetWindowThreadProcessId(h, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public Native.RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

    /// <summary>
    /// A window counts as fullscreen when Windows itself reports an exclusive D3D fullscreen app, or when the window
    /// covers its whole monitor (borderless/windowed-fullscreen games, F11 browsers). Only the primary monitor matters.
    /// </summary>
    private static bool IsFullscreen(IntPtr fg)
    {
        if (fg == IntPtr.Zero) return false;
        var cls = new System.Text.StringBuilder(64);
        Win32.GetClassName(fg, cls, 64);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow") return false;

        var mon = MonitorFromWindow(fg, 2 /* NEAREST */);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi) || (mi.dwFlags & 1 /* PRIMARY */) == 0) return false;

        if (SHQueryUserNotificationState(out var state) == 0 && state == 3 /* QUNS_RUNNING_D3D_FULL_SCREEN */) return true;
        if (!Native.GetWindowRect(fg, out var r)) return false;
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    public void Shutdown() => UnregisterAppBar();

    // ---------- apps ----------

    private sealed class AppButton
    {
        public AppEntry App;
        public Border Root;
        public Border Indicator;
        public Image Icon;
        public ScaleTransform Scale;
        public bool Active;
    }

    private const uint EVENT_SYSTEM_FOREGROUND = 3, EVENT_SYSTEM_MINIMIZESTART = 0x16, EVENT_SYSTEM_MINIMIZEEND = 0x17;
    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    private WinEventProc _winEventProc; // kept alive for the GC
    private readonly Dictionary<string, AppButton> _buttons = new();
    private static readonly IEasingFunction Spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 };
    private static readonly IEasingFunction Smooth = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly Brush IdleDot = new SolidColorBrush(Color.FromArgb(0xB0, 255, 255, 255));
    private int _tick;

    /// <summary>Foreground / minimize changes arrive as events, so the active app is highlighted immediately instead of on the next poll.</summary>
    private void HookWindowEvents()
    {
        _winEventProc = (_, _, _, _, _, _, _) => Dispatcher.BeginInvoke(RefreshApps);
        SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventProc, 0, 0, 0);
        SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _winEventProc, 0, 0, 0);
    }

    private void Refresh()
    {
        TimeText.Text = DateTime.Now.ToString("HH:mm");
        DateText.Text = DateTime.Now.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("de-DE"));
        UpdateVolumeIcon();
        if (_tick++ % 6 == 0) UpdateNetworkIcon();
        RefreshApps();
    }

    private void RefreshApps()
    {
        CheckFullscreen();
        List<AppEntry> apps;
        try { apps = TaskbarApps.Snapshot(); } catch { return; }
        var fg = Win32.GetForegroundWindow();

        // Reuse existing buttons; only touch the panel when apps were added, removed or reordered.
        var keys = apps.Select(a => a.Key).ToList();
        bool layoutChanged = keys.Count != AppsPanel.Children.Count ||
                             keys.Where((k, i) => !ReferenceEquals(AppsPanel.Children[i], _buttons.GetValueOrDefault(k)?.Root)).Any();
        foreach (var app in apps)
        {
            if (!_buttons.TryGetValue(app.Key, out var btn)) _buttons[app.Key] = btn = BuildAppButton(app);
            btn.App = app;
            SetState(btn, app.Windows.Contains(fg), app.Windows.Count > 0);
        }
        if (!layoutChanged) return;
        foreach (var gone in _buttons.Keys.Except(keys).ToList()) _buttons.Remove(gone);
        AppsPanel.Children.Clear();
        foreach (var k in keys) AppsPanel.Children.Add(_buttons[k].Root);
    }

    private void SetState(AppButton btn, bool active, bool running)
    {
        double width = !running ? 0 : active ? 16 : 6;
        if (btn.Indicator.Width != width || btn.Active != active)
            btn.Indicator.BeginAnimation(WidthProperty, new DoubleAnimation(width, Theme.Ms(220)) { EasingFunction = Smooth });
        btn.Indicator.Background = active ? Theme.AccentBrush : IdleDot;
        if (btn.Active == active) return;
        btn.Active = active;
        btn.Root.Tag = active;
        if (!btn.Root.IsMouseOver) btn.Root.Background = active ? HoverBrush : NoBrush;
    }

    private AppButton BuildAppButton(AppEntry app)
    {
        var btn = new AppButton { App = app, Scale = new ScaleTransform(1, 1) };
        var grid = new Grid { RenderTransform = btn.Scale, RenderTransformOrigin = new Point(0.5, 0.5) };
        var img = new Image
        {
            Source = app.Icon, Width = Settings.Current.TaskbarIconSize, Height = Settings.Current.TaskbarIconSize,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        btn.Icon = img;
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        grid.Children.Add(img);
        btn.Indicator = new Border
        {
            Width = 0, Height = 3, CornerRadius = new CornerRadius(1.5), Background = IdleDot,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 1),
        };
        grid.Children.Add(btn.Indicator);

        btn.Root = new Border
        {
            Width = 46, Height = Settings.Current.TaskbarHeight - 6, CornerRadius = new CornerRadius(12), Margin = new Thickness(1, 0, 1, 0),
            Background = NoBrush, Child = grid, ToolTip = app.Name, Tag = false,
        };
        btn.Root.MouseEnter += Hover_Enter;
        btn.Root.MouseLeave += Hover_Leave;
        btn.Root.MouseLeftButtonDown += (_, _) => Press(btn, 0.84, 90, Smooth);
        btn.Root.MouseLeftButtonUp += (_, _) => { Press(btn, 1, 260, Spring); AppClick(btn); };
        btn.Root.MouseRightButtonUp += (_, e) => { AppMenu(btn.App, btn.Root); e.Handled = true; };
        return btn;
    }

    private static void Press(AppButton btn, double to, int ms, IEasingFunction ease)
    {
        var a = new DoubleAnimation(to, Theme.Ms(ms)) { EasingFunction = ease };
        btn.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        btn.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    private void AppClick(AppButton btn)
    {
        var app = btn.App;
        if (app.Windows.Count == 0) { Launch(app); return; }
        var fg = Win32.GetForegroundWindow();
        var idx = app.Windows.IndexOf(fg);
        if (idx >= 0 && app.Windows.Count == 1)
        {
            Win32.ShowWindow(fg, Win32.SW_MINIMIZE);
            SetState(btn, false, true);
            return;
        }
        // Show the result right away; the foreground event confirms it a moment later.
        foreach (var other in _buttons.Values) if (other != btn) SetState(other, false, other.App.Windows.Count > 0);
        SetState(btn, true, true);
        // Cycle through the app's windows when it has several.
        Win32.Activate(app.Windows[(idx + 1) % app.Windows.Count]);
    }

    private static void Launch(AppEntry app)
    {
        try { Process.Start(new ProcessStartInfo(app.LaunchPath) { UseShellExecute = true }); } catch { }
    }

    private void AppMenu(AppEntry app, FrameworkElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        var open = new MenuItem { Header = app.Windows.Count > 0 ? "Neues Fenster" : "Öffnen", Icon = app.Windows.Count > 0 ? "" : "" };
        open.Click += (_, _) => Launch(app);
        menu.Items.Add(open);
        if (app.Windows.Count > 0)
        {
            var close = new MenuItem { Header = app.Windows.Count > 1 ? "Alle Fenster schließen" : "Fenster schließen", Icon = "" };
            close.Click += (_, _) => app.Windows.ForEach(w => Win32.PostMessage(w, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero));
            menu.Items.Add(close);
        }
        menu.IsOpen = true;
    }

    /// <summary>Right click on the bar itself (not on an app): Task Manager and taskbar settings, like the Windows taskbar.</summary>
    private void Bar_RightClick(object sender, MouseButtonEventArgs e)
    {
        // Opens just above the bar, centred on the click, instead of at the raw mouse point (which overlapped the bar).
        var x = e.GetPosition(Bg).X;
        var menu = new ContextMenu { PlacementTarget = Bg, Placement = System.Windows.Controls.Primitives.PlacementMode.Top, HorizontalOffset = x - 80, VerticalOffset = -8 };
        void Item(string glyph, string header, Action onClick)
        {
            var item = new MenuItem { Header = header, Icon = glyph };
            item.Click += (_, _) => onClick();
            menu.Items.Add(item);
        }
        Item("", "Task-Manager", () => Open("taskmgr.exe"));
        Item("", "Taskleisteneinstellungen", () => Flyout.Toggle(FlyoutKind.TaskbarSettings));
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    private void Hover_Enter(object sender, MouseEventArgs e) => ((Border)sender).Background = HoverBrush;
    private void Hover_Leave(object sender, MouseEventArgs e)
    {
        var b = (Border)sender;
        b.Background = b.Tag is true ? HoverBrush : NoBrush;
        // Released outside the icon: spring back instead of staying pressed.
        if (_buttons.Values.FirstOrDefault(x => x.Root == b) is { } btn) Press(btn, 1, 200, Smooth);
    }

    // ---------- start / tray ----------

    private void Start_Click(object sender, MouseButtonEventArgs e) => ((App)Application.Current).StartMenu.Toggle();
    private static FlyoutWindow Flyout => ((App)Application.Current).Flyout;
    private void Network_Click(object sender, RoutedEventArgs e) => Flyout.Toggle(FlyoutKind.Network);
    private void Volume_Click(object sender, RoutedEventArgs e) => Flyout.Toggle(FlyoutKind.Volume);
    private void Center_Click(object sender, RoutedEventArgs e) => Flyout.Toggle(FlyoutKind.ActionCenter);

    private void Vol_Wheel(object sender, MouseWheelEventArgs e)
    {
        Volume.Set(Volume.Get() + (e.Delta > 0 ? 0.02f : -0.02f));
        UpdateVolumeIcon();
    }

    private void UpdateNetworkIcon()
    {
        var active = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
            n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.GetIPProperties().GatewayAddresses.Count > 0);
        NetBtn.Content = active == null ? "" : active.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 ? "" : "";
    }

    private void UpdateVolumeIcon()
    {
        var v = Volume.Get();
        VolBtn.Content = Volume.IsMuted() ? "" : v < 0.33 ? "" : v < 0.66 ? "" : "";
    }

    // ---------- media ----------

    private void UpdateMedia()
    {
        MediaChip.Visibility = _media.HasSession ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Text = _media.Title;
        ArtistText.Text = _media.Artist;
        PlayBtn.Content = _media.IsPlaying ? "" : "";
        var art = _media.Art;
        Art.Background = art != null ? new ImageBrush(art) { Stretch = Stretch.UniformToFill } : new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255));
        ArtIcon.Visibility = art == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Media_MouseEnter(object sender, MouseEventArgs e) =>
        MediaControls.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Theme.Ms(150)));
    private void Media_MouseLeave(object sender, MouseEventArgs e) =>
        MediaControls.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Theme.Ms(150)));

    private void Art_Click(object sender, MouseButtonEventArgs e) => _media.PlayPause();
    private void Prev_Click(object sender, RoutedEventArgs e) => _media.Previous();
    private void Play_Click(object sender, RoutedEventArgs e) => _media.PlayPause();
    private void Next_Click(object sender, RoutedEventArgs e) => _media.Next();
}
