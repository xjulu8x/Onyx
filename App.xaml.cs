using System.Windows;

namespace DesktopOverlay;

public partial class App : Application
{
    private static Mutex _single;
    private readonly List<Window> _widgets = new();

    public IslandWindow Island { get; private set; }
    public TaskbarWindow Taskbar { get; private set; }
    public StartMenuWindow StartMenu { get; private set; }
    public FlyoutWindow Flyout { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Drawing without the GPU saves about 45 MB, but animations may be less smooth, so it is opt-in. Must be set before any window exists.
        if (Settings.Current.SaveMemory) System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
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
        // Test aid: "--update-now [sha256]" runs the update against the real release (optionally with a wrong checksum) and exits.
        var update = Array.IndexOf(e.Args, "--update-now");
        if (update >= 0) { TestUpdate(update + 1 < e.Args.Length ? e.Args[update + 1] : null); return; }
        // Test aid: "--cards <dir>" only builds the desktop cards, lets them run a few seconds and saves each as a PNG.
        // It touches neither the taskbar nor any hook, so it can run next to a normal Onyx.
        var cards = Array.IndexOf(e.Args, "--cards");
        if (cards >= 0 && cards + 1 < e.Args.Length) { RenderCards(e.Args[cards + 1]); return; }
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
            new RoutedEventHandler((s, _) =>
            {
                var menu = (System.Windows.Controls.ContextMenu)s;
                MenuClickWatch.Start(menu);
                // Menus fade in quickly instead of popping up.
                menu.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, Theme.Ms(110)));
            }));

        // 2.0: the island hangs as a notch at the top edge, and the taskbar is a dock with apps only.
        if (Settings.Current.Layout < 2) { Settings.Current.IslandShape = 1; Settings.Current.TaskbarShape = 1; Settings.Current.Layout = 2; Settings.Current.Save(); }
        // 2.2: terminal look: flat dark sheets, thin lines, dot grid, almost square corners. The glass look is gone.
        if (Settings.Current.Layout < 3)
        {
            var s = Settings.Current;
            Theme.UsePresetValues(0);
            (s.TaskbarRadius, s.IslandCorners, s.RimStyle, s.Grain, s.IndicatorStyle, s.IconTint) = (4, 0.3, 1, true, 1, 0);
            s.FontFamily = Theme.Fonts[0].Family;
            (s.DayTheme, s.NightTheme) = ("Mono", "Mono");
            s.Layout = 3;
            s.Save();
        }
        Theme.ApplyFont();
        Island = new IslandWindow();
        Island.Show();
        Taskbar = new TaskbarWindow();
        Taskbar.Show();
        StartMenu = new StartMenuWindow();
        Flyout = new FlyoutWindow();
        KeyboardHook.WinKeyPressed += () => { Taskbar.Peek(); StartMenu.Toggle(); };
        KeyboardHook.Install();
        // The × of the arrange mode: hide the widget and remember it, it comes back under Einstellungen → Widgets.
        WidgetMover.Remove = w => { Settings.Current.Cards[KeyOf(w)] = false; Settings.Current.Save(); w.Hide(); };
        _widgets.Add(new StatsWindow());
        _widgets.Add(new TempWindow());
        // Cards of the terminal look (2.3)
        _widgets.AddRange(new Window[] { new MusicCard(), new TerminalCard(), new BonsaiCard(), new WeatherCard(), new ClockCard(), new CalendarCard(), new SignalCard(), new KoiCard() });
        ApplyWidgetLayout();
        ApplyWidgetLook();
        TaskbarHider.Start();
        Task.Run(Settings.MigrateAndRepair);
        Taskbar.StartTray();
        UpdateService.Start();
        Theme.StartAutoTheme();

        if (e.Args.Contains("--probe")) Probe();
        if (e.Args.Contains("--demo")) Island.Demo();
        var snap = Array.IndexOf(e.Args, "--snapshot");
        if (snap >= 0 && snap + 1 < e.Args.Length) Snapshot(e.Args[snap + 1], e.Args.Contains("tray") ? FlyoutKind.Tray : FlyoutKind.ActionCenter);
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

    private SettingsWindow _settings;

    /// <summary>Opens the settings, or brings them to the front. Closing frees the window, so it costs nothing until needed.</summary>
    public void OpenSettings()
    {
        if (_settings == null)
        {
            _settings = new SettingsWindow();
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }
        Win32.Activate(new System.Windows.Interop.WindowInteropHelper(_settings).Handle);
    }

    /// <summary>Installs the update found by <see cref="UpdateService"/> and restarts into the new version.</summary>
    public async void InstallUpdate()
    {
        if (!await UpdateService.InstallAsync()) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--wait") { UseShellExecute = false });
        Quit();
    }

    private async void TestUpdate(string sha256)
    {
        await UpdateService.CheckAsync();
        bool installed = await UpdateService.InstallAsync(sha256);
        System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "onyx_update.txt"), $"available={UpdateService.Available} installed={installed}");
        Shutdown();
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
            $"cpu={s.CpuTemp} gpu={s.GpuTemp} elevated={SystemMonitor.IsElevated}\ntray: {string.Join(" | ", Tray.Visible().Select(i => (i.Title ?? "").ReplaceLineEndings(" ")))}\npinned: " +string.Join(" | ", TaskbarApps.Pinned().Select(p => p.Name + (p.Icon == null ? " (no icon)" : ""))));
        Quit();
    }

    /// <summary>Debug aid: renders every window to PNG files and exits.</summary>
    private async void Snapshot(string dir, FlyoutKind flyout)
    {
        foreach (Window w in Windows) if (w is IslandWindow iw) iw.ForceExpand();
        // Opened without focus so nothing typed elsewhere ends up in them.
        StartMenu.Toggle(activate: false);
        Flyout.Toggle(flyout, activate: false);
        await Task.Delay(4000);
        System.IO.Directory.CreateDirectory(dir);
        Island.RenderPages(dir);
        var settings = new SettingsWindow { ShowActivated = false };
        settings.Show();
        settings.RenderPages(dir);
        settings.Close();
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

    /// <summary>The widget windows on screen: with "Einzeln" the stats window is replaced by its parts.</summary>
    private IEnumerable<Window> Widgets => _widgets.SelectMany(w => w is StatsWindow { Parts.Count: > 0 } stats ? stats.Parts : new[] { w });

    private async void RenderCards(string dir)
    {
        Pulse.Force = true;
        Theme.ApplyFont();
        var list = new Window[] { new MusicCard(), new TerminalCard(), new BonsaiCard(), new WeatherCard(), new ClockCard(), new CalendarCard(), new SignalCard(), new KoiCard() };
        var args = Environment.GetCommandLineArgs();
        int only = Array.IndexOf(args, "--only");
        if (only >= 0 && only + 1 < args.Length) { foreach (var w in list.Where(w => w.GetType().Name != args[only + 1])) w.Close(); list = list.Where(w => w.GetType().Name == args[only + 1]).ToArray(); }
        foreach (var w in list) w.Show();
        Pulse.Refresh();
        System.IO.Directory.CreateDirectory(dir);
        for (int shot = 0; shot < 2; shot++)
        {
            await Task.Delay(3000);
            foreach (var w in list)
            {
                var el = (FrameworkElement)w.Content;
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(el);
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(el.ActualWidth * dpi.DpiScaleX), (int)(el.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(el);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using var fs = System.IO.File.Create(System.IO.Path.Combine(dir, $"{w.GetType().Name}-{shot}.png"));
                enc.Save(fs);
            }
            if (shot == 0 && list.OfType<TerminalCard>().FirstOrDefault() is { } term) term.Show(1);
        }
        try
        {
            var settings = new SettingsWindow { ShowActivated = false, Left = -3000 };
            settings.Show();
            settings.RenderPages(dir);
            settings.Close();
        }
        catch (Exception ex) { System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "settings-error.txt"), ex.ToString()); }
        if (Environment.GetCommandLineArgs().Contains("--hold")) await Task.Delay(30000); // test aid: keep running to measure the load
        Shutdown();
    }

    /// <summary>Name under which a widget is switched on or off: cards by their key, the parts of the system widget by their title.</summary>
    public static string KeyOf(Window w) => w switch { Card c => c.Key, StatsWindow => "Stats", TempWindow => "Temp", _ => w.Title };

    public T Widget<T>() where T : Window => _widgets.OfType<T>().FirstOrDefault();

    public void ApplyWidgetLayout()
    {
        foreach (var stats in _widgets.OfType<StatsWindow>()) stats.ApplyLayout();
        ApplyWidgetVisibility();
        ApplyWidgetLook();
    }

    public void ApplyWidgetVisibility()
    {
        foreach (var w in Widgets)
            if (Settings.Current.ShowWidgets && Settings.Current.CardOn(KeyOf(w))) w.Show(); else w.Hide();
        Pulse.Refresh();
    }

    public void ApplyWidgetLook()
    {
        var s = Settings.Current;
        var brush = new System.Windows.Media.SolidColorBrush(Settings.ToColor(s.WidgetColor, 0xFF, System.Windows.Media.Colors.White));
        brush.Freeze();
        foreach (var stats in _widgets.OfType<StatsWindow>()) stats.Foreground = brush; // its graphs follow, also when hidden behind its parts
        foreach (var w in Widgets)
        {
            w.Foreground = brush;
            if (w is Card card) card.SetAlpha(s.WidgetOpacity); else w.Opacity = s.WidgetOpacity;
            // Same surface as dock and island: flat, or with the dot grid ("Oberfläche").
            if (w.Content is System.Windows.Controls.Border frame) frame.Background = Theme.Surface("#0A0A0A", 0xF8);
            // Grows and shrinks around the right edge, so widgets next to the screen edge stay on screen.
            double right = w.Left + w.ActualWidth;
            ((FrameworkElement)w.Content).LayoutTransform = new System.Windows.Media.ScaleTransform(s.WidgetScale, s.WidgetScale);
            if (w is StatsWindow || w.Tag is "wide") w.Width = 250 * s.WidgetScale;
            if (!w.IsLoaded || double.IsNaN(right)) continue;
            w.UpdateLayout();
            w.Left = right - w.ActualWidth;
        }
    }

    private void RestoreShell()
    {
        KeyboardHook.Uninstall();
        Tray.Stop();
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
