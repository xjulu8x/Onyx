using System.Windows;

namespace DesktopOverlay;

public partial class App : Application
{
    private static Mutex _single;
    private readonly List<Window> _widgets = new();

    public TaskbarWindow Taskbar { get; private set; }
    public StartMenuWindow StartMenu { get; private set; }
    public FlyoutWindow Flyout { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); // netsh prints in the OEM code page

        // Test aid: "--autostart on|off" runs the same code as the "Mit Windows starten" switch and exits.
        var auto = Array.IndexOf(e.Args, "--autostart");
        if (auto >= 0 && auto + 1 < e.Args.Length)
        {
            Settings.Autostart = e.Args[auto + 1] == "on";
            System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "onyx_autostart.txt"), $"autostart={Settings.Autostart}");
            Shutdown();
            return;
        }
        _single = new Mutex(false, "Onyx.SingleInstance");
        bool owned;
        // "--wait": started by RestartElevated, so give the old instance a moment to exit.
        try { owned = _single.WaitOne(e.Args.Contains("--wait") ? 10000 : 0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) { Shutdown(); return; }

        // Never leave the user without a taskbar, even if something crashes.
        DispatcherUnhandledException += (_, _) => RestoreShell();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => RestoreShell();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreShell();

        // Our windows never take focus, so a context menu wouldn't notice clicks elsewhere on the screen.
        // While a menu is open, a mouse hook closes it on any click outside of it.
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ContextMenu), System.Windows.Controls.ContextMenu.OpenedEvent,
            new RoutedEventHandler((s, _) => MenuClickWatch.Start((System.Windows.Controls.ContextMenu)s)));

        new IslandWindow().Show();
        Taskbar = new TaskbarWindow();
        Taskbar.Show();
        StartMenu = new StartMenuWindow();
        Flyout = new FlyoutWindow();
        KeyboardHook.WinKeyPressed += () => { Taskbar.Peek(); StartMenu.Toggle(); };
        KeyboardHook.Install();
        _widgets.Add(new StatsWindow());
        _widgets.Add(new TempWindow());
        ApplyWidgetVisibility();
        ApplyWidgetColor();
        TaskbarHider.Start();
        Task.Run(Settings.MigrateAndRepair);

        if (e.Args.Contains("--probe")) Probe();
        var snap = Array.IndexOf(e.Args, "--snapshot");
        if (snap >= 0 && snap + 1 < e.Args.Length) Snapshot(e.Args[snap + 1]);
    }

    /// <summary>Restarts the overlay with admin rights (needed for the CPU temperature sensor). Shows the UAC prompt.</summary>
    public void RestartElevated()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--wait") { UseShellExecute = true, Verb = "runas" });
            Quit();
        }
        catch { /* UAC prompt cancelled */ }
    }

    /// <summary>Fixes whatever keeps the CPU temperature from showing: admin rights first, then the sensor driver.</summary>
    public async void FixCpuTemp()
    {
        if (!SystemMonitor.IsElevated) RestartElevated();
        else if (!SystemMonitor.DriverInstalled) await SystemMonitor.InstallDriver();
    }

    /// <summary>Debug aid: writes the sensor readings to %TEMP%\overlay_probe.txt and exits.</summary>
    private async void Probe()
    {
        await Task.Delay(7000);
        var s = SystemMonitor.Instance;
        System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "overlay_probe.txt"),
            $"cpu={s.CpuTemp} gpu={s.GpuTemp} elevated={SystemMonitor.IsElevated}\npinned: " + string.Join(" | ", TaskbarApps.Pinned().Select(p => p.Name + (p.Icon == null ? " (no icon)" : ""))));
        Quit();
    }

    /// <summary>Debug aid: renders every window to PNG files and exits.</summary>
    private async void Snapshot(string dir)
    {
        foreach (Window w in Windows) if (w is IslandWindow iw) iw.ForceExpand();
        // Opened without focus so nothing typed elsewhere ends up in them.
        StartMenu.Toggle(activate: false);
        Flyout.Toggle(FlyoutKind.ActionCenter, activate: false);
        await Task.Delay(4000);
        System.IO.Directory.CreateDirectory(dir);
        foreach (Window w in Windows)
        {
            if (w.Content is not System.Windows.FrameworkElement el || el.ActualWidth < 1) continue;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(el);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(el.ActualWidth * dpi.DpiScaleX), (int)(el.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(el);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = System.IO.File.Create(System.IO.Path.Combine(dir, w.GetType().Name + ".png"));
            enc.Save(fs);
        }
        Quit();
    }

    public void ApplyWidgetVisibility()
    {
        foreach (var w in _widgets)
            if (Settings.Current.ShowWidgets) w.Show(); else w.Hide();
    }

    public void ApplyWidgetColor()
    {
        var brush = new System.Windows.Media.SolidColorBrush(Settings.ToColor(Settings.Current.WidgetColor, 0xFF, System.Windows.Media.Colors.White));
        brush.Freeze();
        foreach (var w in _widgets) w.Foreground = brush;
    }

    private void RestoreShell()
    {
        KeyboardHook.Uninstall();
        try { Taskbar?.Shutdown(); } catch { }
        TaskbarHider.Restore();
    }

    public void Quit()
    {
        RestoreShell();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        RestoreShell();
        base.OnExit(e);
    }
}
