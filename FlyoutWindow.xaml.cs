using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DesktopOverlay;

public enum FlyoutKind { Network, Volume, ActionCenter, TaskbarSettings, Tray, Design }

/// <summary>Taskbar pop-ups (network, volume, action center) in the overlay's own look, opened bottom-right above the taskbar.</summary>
public partial class FlyoutWindow : Window
{
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private FlyoutKind? _kind;
    private bool _closing;
    private DateTime _closedAt;
    private readonly DispatcherTimer _clock;
    private TextBlock _bigTime;

    public FlyoutWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.SetWindowLong(h, Native.GWL_EXSTYLE, Native.GetWindowLong(h, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW);
        };
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => { if (_bigTime != null) _bigTime.Text = DateTime.Now.ToString("HH:mm:ss"); };
        // The clock only ticks while the flyout is open.
        IsVisibleChanged += (_, _) => { if (IsVisible) _clock.Start(); else _clock.Stop(); };
        // Wi-Fi list and notifications load after opening; keep the flyout anchored above the taskbar as it grows.
        SizeChanged += (_, _) => { if (IsVisible) Reposition(); };
    }

    // ---------- open / close ----------

    public void Toggle(FlyoutKind kind, bool activate = true)
    {
        // A click on the tray button deactivates (closes) us first; don't reopen on that same click.
        if ((DateTime.Now - _closedAt).TotalMilliseconds < 250 && _kind == kind) return;
        if (IsVisible && !_closing && _kind == kind) { CloseFlyout(); return; }

        _kind = kind;
        _closing = false;
        Host.Content = kind switch
        {
            FlyoutKind.Network => BuildNetwork(),
            FlyoutKind.Volume => BuildVolume(),
            FlyoutKind.TaskbarSettings => BuildTaskbarSettings(),
            FlyoutKind.Tray => BuildTray(),
            FlyoutKind.Design => BuildDesign(),
            _ => BuildActionCenter(),
        };
        Panel.Opacity = 0;
        ShowActivated = activate;
        Show();
        Scroller.MaxHeight = SystemParameters.WorkArea.Height - 70; // window margin + panel padding
        Scroller.ScrollToTop();
        UpdateLayout();
        Reposition();
        if (activate) Win32.Activate(new WindowInteropHelper(this).Handle);
        Panel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Theme.Ms(180)) { EasingFunction = Ease });
        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24, 0, Theme.Ms(280)) { EasingFunction = Ease });
    }

    private void Reposition()
    {
        Left = SystemParameters.PrimaryScreenWidth - ActualWidth - 2;
        Top = Math.Max(SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Bottom - ActualHeight + 4);
    }

    private void CloseFlyout()
    {
        if (!IsVisible || _closing) return;
        _closing = true;
        _closedAt = DateTime.Now;
        var fade = new DoubleAnimation(0, Theme.Ms(130));
        // The content is rebuilt on every open, so nothing has to stay in memory while closed.
        fade.Completed += (_, _) => { if (_closing) { Hide(); _closing = false; Host.Content = null; } };
        Panel.BeginAnimation(OpacityProperty, fade);
        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, Theme.Ms(130)));
    }

    private void Window_Deactivated(object sender, EventArgs e) => CloseFlyout();
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) CloseFlyout(); }

    private void Run(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
        CloseFlyout();
    }

    // ---------- building blocks ----------

    private Brush Dim => (Brush)FindResource("Dim");
    private FontFamily Icons => (FontFamily)FindResource("Icons");

    private TextBlock Header(string text) => new() { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 12) };

    private TextBlock Glyph(string g, double size = 16) => new() { Text = g, FontFamily = Icons, FontSize = size, VerticalAlignment = VerticalAlignment.Center };

    private Button LinkButton(string text, string target)
    {
        var b = new Button { Style = (Style)FindResource("Row"), Margin = new Thickness(0, 10, 0, 0) };
        var g = new Grid();
        g.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var arrow = Glyph("", 11);
        arrow.HorizontalAlignment = HorizontalAlignment.Right;
        g.Children.Add(arrow);
        b.Content = g;
        b.Click += (_, _) => Run(target);
        return b;
    }

    private FrameworkElement VolumeRow()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        var mute = new Button { Style = (Style)FindResource("IconButton"), FontSize = 16, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(-6, 0, 8, 0) };
        var slider = new VolumeSlider();
        var pct = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        slider.Changed += (v, m) => { pct.Text = $"{v * 100:0}%"; mute.Content = VolumeSlider.Glyph(v, m); };
        mute.Click += (_, _) => { Volume.ToggleMute(); slider.Refresh(); };
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(pct, 2);
        g.Children.Add(mute); g.Children.Add(slider); g.Children.Add(pct);
        slider.Loaded += (_, _) => slider.Refresh();
        return g;
    }

    private FrameworkElement MediaCard()
    {
        var m = MediaService.Instance;
        if (!m.HasSession) return null;
        var card = new Border { Background = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255)), CornerRadius = new CornerRadius(16), Padding = new Thickness(10), Margin = new Thickness(0, 14, 0, 0) };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(10),
            Background = m.Art != null ? new ImageBrush(m.Art) { Stretch = Stretch.UniformToFill } : new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)),
        });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
        text.Children.Add(new TextBlock { Text = m.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = m.Artist, FontSize = 11, Foreground = Dim, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (glyph, act) in new (string, Action)[] { ("", m.Previous), (m.IsPlaying ? "" : "", m.PlayPause), ("", m.Next) })
        {
            var b = new Button { Style = (Style)FindResource("IconButton"), Content = glyph, FontSize = 13 };
            b.Click += (_, _) => act();
            controls.Children.Add(b);
        }
        Grid.SetColumn(controls, 2);
        g.Children.Add(controls);
        card.Child = g;
        return card;
    }

    // ---------- volume ----------

    private FrameworkElement BuildVolume()
    {
        var root = new StackPanel();
        root.Children.Add(Header("Lautstärke"));
        root.Children.Add(VolumeRow());
        if (MediaCard() is { } media) root.Children.Add(media);
        root.Children.Add(LinkButton("Sound-Einstellungen", "ms-settings:sound"));
        return root;
    }

    // ---------- design ----------

    private TextBlock SectionTitle(string text) => new() { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Dim, Margin = new Thickness(2, 14, 0, 6) };

    /// <summary>Slider for a setting between min and max; changes apply live and are saved right away.</summary>
    private FrameworkElement DesignSlider(string label, double min, double max, Func<double> get, Action<double> set, Func<double, string> format)
    {
        var root = new StackPanel { Margin = new Thickness(2, 0, 0, 2) };
        var head = new Grid();
        head.Children.Add(new TextBlock { Text = label, FontSize = 12 });
        var value = new TextBlock { FontSize = 12, Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Right };
        head.Children.Add(value);
        root.Children.Add(head);
        var slider = new VolumeSlider(() => (float)((get() - min) / (max - min)), v =>
        {
            set(min + v * (max - min));
            Settings.Current.Save();
        });
        slider.Changed += (_, _) => value.Text = format(get());
        slider.Loaded += (_, _) => slider.Refresh();
        root.Children.Add(slider);
        return root;
    }

    /// <summary>Row of pills of which one is selected.</summary>
    private FrameworkElement Choice(string[] options, int selected, Action<int> pick)
    {
        var row = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Margin = new Thickness(0, 0, 0, 4) };
        var on = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255));
        var off = new SolidColorBrush(Color.FromArgb(0x10, 255, 255, 255));
        for (int i = 0; i < options.Length; i++)
        {
            int index = i;
            var chip = new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(6, 6, 6, 6), Margin = new Thickness(2), Cursor = Cursors.Hand,
                Background = i == selected ? on : off,
                Child = new TextBlock { Text = options[i], FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
            };
            chip.MouseLeftButtonUp += (_, _) =>
            {
                foreach (Border c in row.Children) c.Background = c == chip ? on : off;
                pick(index);
            };
            row.Children.Add(chip);
        }
        return row;
    }

    private FrameworkElement BuildDesign()
    {
        var s = Settings.Current;
        var app = (App)Application.Current;
        var bar = app.Taskbar;
        var root = new StackPanel();
        root.Children.Add(Header("Design"));

        root.Children.Add(new TextBlock { Text = "Theme", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Dim, Margin = new Thickness(2, 0, 0, 6) });
        root.Children.Add(Choice(Theme.Presets.Select(p => p.Name).ToArray(), Array.FindIndex(Theme.Presets, p => p.Accent == s.AccentColor && p.Island == s.IslandColor), i =>
        {
            Theme.UsePreset(i);
            Host.Content = BuildDesign(); // the switches and values shown here changed too
        }));

        void Pick(string title, string[] options, int selected, Action<int> apply, StackPanel into)
        {
            into.Children.Add(SectionTitle(title));
            into.Children.Add(Choice(options, selected, i => { apply(i); s.Save(); }));
        }

        var general = Group(root, "Allgemein");
        Pick("Akzentfarbe", new[] { "Blau", "Hellblau", "Grau", "Weiß" }, Array.IndexOf(Theme.AccentColors, s.AccentColor), i => { s.AccentColor = Theme.AccentColors[i]; Theme.ApplyAll(); }, general);
        Pick("Schrift", Theme.Fonts.Select(f => f.Label).ToArray(), Array.FindIndex(Theme.Fonts, f => f.Family == s.FontFamily), i => { s.FontFamily = Theme.Fonts[i].Family; Theme.ApplyFont(); }, general);
        Pick("Animationen", new[] { "Schnell", "Normal", "Aus" }, s.AnimationSpeed, i => s.AnimationSpeed = i, general);
        Pick("Rand", new[] { "Keiner", "Dezent", "Akzent", "Lichtlauf" }, s.RimStyle, i => { s.RimStyle = i; Theme.ApplyAll(); }, general);
        Pick("Oberfläche", new[] { "Glatt", "Glas-Körnung" }, s.Grain ? 1 : 0, i => { s.Grain = i == 1; Theme.ApplyAll(); }, general);

        var taskbar = Group(root, "Taskleiste");
        Pick("Form", new[] { "Leiste", "Dock", "Drei Inseln" }, s.TaskbarShape, i => { s.TaskbarShape = i; bar.ApplyLook(); }, taskbar);
        Pick("App-Symbole", new[] { "Farbig", "Weiß", "Grau", "Akzent" }, s.IconTint, i => { s.IconTint = i; bar.ApplyLook(); }, taskbar);
        Pick("App-Anzeige", new[] { "Strich", "Punkt", "Leuchten", "Aus" }, s.IndicatorStyle, i => { s.IndicatorStyle = i; bar.ApplyLook(); }, taskbar);
        Pick("Uhr", new[] { "Mit Datum", "Nur Zeit", "Groß" }, s.ClockStyle, i => { s.ClockStyle = i; bar.ApplyLook(); }, taskbar);
        Pick("Apps", new[] { "Mittig", "Links" }, s.TaskbarAppsLeft ? 1 : 0, i => { s.TaskbarAppsLeft = i == 1; bar.ApplyLook(); }, taskbar);
        taskbar.Children.Add(SectionTitle("Größe"));
        taskbar.Children.Add(DesignSlider("Höhe", 38, 64, () => s.TaskbarHeight, v => { s.TaskbarHeight = Math.Round(v); bar.ApplyLook(); }, v => $"{v:0} px"));
        taskbar.Children.Add(DesignSlider("Symbolgröße", 18, 32, () => s.TaskbarIconSize, v => { s.TaskbarIconSize = Math.Round(v); bar.ApplyLook(); }, v => $"{v:0} px"));
        taskbar.Children.Add(DesignSlider("Ecken", 0, 25, () => s.TaskbarRadius, v => { s.TaskbarRadius = Math.Round(v); bar.ApplyLook(); }, v => $"{v:0} px"));

        var island = Group(root, "Island");
        island.Children.Add(DesignSlider("Größe", 0.8, 1.2, () => s.IslandScale, v => { s.IslandScale = Math.Round(v, 2); app.Island.ApplyLook(); }, v => $"{v * 100:0} %"));
        island.Children.Add(DesignSlider("Ecken", 0.2, 1, () => s.IslandCorners, v => { s.IslandCorners = Math.Round(v, 2); app.Island.ApplyLook(); }, v => $"{v * 100:0} %"));
        island.Children.Add(DesignSlider("Deckkraft", 0.4, 1, () => s.IslandOpacity, v => { s.IslandOpacity = Math.Round(v, 2); app.Island.ApplyLook(); }, v => $"{v * 100:0} %"));

        var widgets = Group(root, "Widgets");
        widgets.Children.Add(DesignSlider("Größe", 0.7, 1.3, () => s.WidgetScale, v => { s.WidgetScale = Math.Round(v, 2); app.ApplyWidgetLook(); }, v => $"{v * 100:0} %"));
        widgets.Children.Add(DesignSlider("Deckkraft", 0.3, 1, () => s.WidgetOpacity, v => { s.WidgetOpacity = Math.Round(v, 2); app.ApplyWidgetLook(); }, v => $"{v * 100:0} %"));
        return root;
    }

    private static readonly HashSet<string> OpenGroups = new() { "Allgemein" };

    /// <summary>Foldable section, so the page stays short. Returns the panel for its content.</summary>
    private StackPanel Group(StackPanel parent, string title)
    {
        var body = new StackPanel { Margin = new Thickness(0, 0, 0, 6), Visibility = OpenGroups.Contains(title) ? Visibility.Visible : Visibility.Collapsed };
        var head = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(2, 12, 2, 4) };
        head.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold });
        var arrow = Glyph(OpenGroups.Contains(title) ? "" : "", 11);
        arrow.HorizontalAlignment = HorizontalAlignment.Right;
        head.Children.Add(arrow);
        head.MouseLeftButtonUp += (_, _) =>
        {
            bool open = body.Visibility != Visibility.Visible;
            body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            arrow.Text = open ? "" : "";
            if (open) OpenGroups.Add(title); else OpenGroups.Remove(title);
        };
        parent.Children.Add(head);
        parent.Children.Add(body);
        return body;
    }

    // ---------- tray overflow ----------

    private FrameworkElement BuildTray()
    {
        var root = new StackPanel();
        root.Children.Add(Header("Weitere Symbole"));
        var icons = new WrapPanel();
        foreach (var icon in Tray.Visible()) icons.Children.Add(Tray.Button(icon));
        root.Children.Add(icons);
        return root;
    }

    // ---------- taskbar settings ----------

    private FrameworkElement BuildTaskbarSettings()
    {
        var s = Settings.Current;
        var bar = ((App)Application.Current).Taskbar;
        var root = new StackPanel();
        root.Children.Add(Header("Taskleiste"));

        // Transparency: 0 % = opaque, 100 % = as see-through as the bar allows
        var label = new Grid { Margin = new Thickness(2, 0, 0, 6) };
        label.Children.Add(new TextBlock { Text = "Transparenz", FontSize = 12, FontWeight = FontWeights.SemiBold });
        var pct = new TextBlock { FontSize = 12, Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Right };
        label.Children.Add(pct);
        root.Children.Add(label);
        var slider = new VolumeSlider(() => (float)s.Transparency, v =>
        {
            s.TaskbarTransparency = Math.Round(v, 2);
            bar.ApplyLook();
            s.Save();
        });
        slider.Changed += (v, _) => pct.Text = $"{v * 100:0} %";
        slider.Loaded += (_, _) => slider.Refresh();
        root.Children.Add(slider);

        // Auto-hide
        var row = new Grid { Margin = new Thickness(2, 16, 0, 0) };
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = "Automatisch einklappen", FontSize = 12, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = "Erscheint, wenn die Maus den unteren Rand berührt", FontSize = 11, Foreground = Dim, Margin = new Thickness(0, 2, 0, 0) });
        row.Children.Add(text);
        var sw = new ToggleSwitch(s.TaskbarAutoHide) { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        sw.Toggled += v => { s.TaskbarAutoHide = v; s.Save(); bar.ApplyAutoHide(); };
        row.Children.Add(sw);
        root.Children.Add(row);

        root.Children.Add(LinkButton("Windows-Taskleisteneinstellungen", "ms-settings:taskbar"));
        return root;
    }

    // ---------- network ----------

    private FrameworkElement BuildNetwork()
    {
        var root = new StackPanel();
        root.Children.Add(Header("Netzwerk"));

        var active = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
            n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
            n.GetIPProperties().GatewayAddresses.Count > 0);
        bool wifi = active?.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;

        var card = new Border { Background = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255)), CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 12, 14, 12) };
        var cg = new Grid();
        cg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cg.ColumnDefinitions.Add(new ColumnDefinition());
        var iconBg = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Background = new SolidColorBrush(active != null ? Theme.Accent : Color.FromArgb(0x26, 255, 255, 255)) };
        var ic = Glyph(active == null ? "" : wifi ? "" : "", 17);
        ic.HorizontalAlignment = HorizontalAlignment.Center;
        iconBg.Child = ic;
        cg.Children.Add(iconBg);
        var info = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = active == null ? "Nicht verbunden" : wifi ? "WLAN" : "Ethernet", FontSize = 13, FontWeight = FontWeights.SemiBold };
        info.Children.Add(name);
        info.Children.Add(new TextBlock
        {
            Text = active == null ? "Kein Netzwerk" : $"Verbunden · {SystemMonitor.Instance.LocalIp}" + (active.Speed > 0 ? $" · {active.Speed / 1_000_000} Mbit/s" : ""),
            FontSize = 11, Foreground = Dim,
        });
        Grid.SetColumn(info, 1);
        cg.Children.Add(info);
        card.Child = cg;
        root.Children.Add(card);

        root.Children.Add(new TextBlock { Text = "WLAN-Netzwerke", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 14, 0, 6) });
        var list = new StackPanel();
        list.Children.Add(new TextBlock { Text = "Suche…", FontSize = 12, Foreground = Dim, Margin = new Thickness(2, 0, 0, 0) });
        root.Children.Add(list);
        LoadWifi(list, name);

        root.Children.Add(LinkButton("Netzwerkeinstellungen", "ms-settings:network"));
        return root;
    }

    private async void LoadWifi(StackPanel list, TextBlock currentName)
    {
        var (current, networks) = await Task.Run(() =>
        {
            var ifaces = Netsh("wlan show interfaces");
            var ssid = Regex.Match(ifaces, @"^\s*SSID\s*:\s*(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
            var output = Netsh("wlan show networks mode=bssid");
            var nets = new List<(string Ssid, int Signal)>();
            foreach (Match m in Regex.Matches(output, @"^SSID \d+\s*:\s*(.*?)\r?$(.*?)(?=^SSID \d+|\z)", RegexOptions.Multiline | RegexOptions.Singleline))
            {
                var signal = Regex.Match(m.Groups[2].Value, @"Signal\s*:\s*(\d+)\s*%");
                var s = m.Groups[1].Value.Trim();
                if (s.Length > 0) nets.Add((s, signal.Success ? int.Parse(signal.Groups[1].Value) : 0));
            }
            return (ssid, nets.OrderByDescending(n => n.Signal).Take(8).ToList());
        });

        if (current.Length > 0) currentName.Text = current;
        list.Children.Clear();
        if (networks.Count == 0)
        {
            list.Children.Add(new TextBlock { Text = "Keine WLAN-Netzwerke gefunden", FontSize = 12, Foreground = Dim, Margin = new Thickness(2, 0, 0, 0) });
            return;
        }
        foreach (var (ssid, signal) in networks)
        {
            var b = new Button { Style = (Style)FindResource("Row") };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(Glyph(signal > 66 ? "" : signal > 33 ? "" : "", 14));
            var t = new TextBlock { Text = ssid, FontSize = 12, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            var state = new TextBlock { Text = ssid == current ? "Verbunden" : $"{signal}%", FontSize = 11, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(state, 2);
            g.Children.Add(state);
            b.Content = g;
            // Connecting works directly for known networks; new ones need a password, so they go to the Windows dialog.
            b.Click += (_, _) =>
            {
                if (ssid == current) return;
                if (Netsh("wlan show profiles").Contains(ssid)) { _ = Task.Run(() => Netsh($"wlan connect name=\"{ssid}\"")); CloseFlyout(); }
                else Run("ms-availablenetworks:");
            };
            list.Children.Add(b);
        }
    }

    private static string Netsh(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netsh.exe", args)
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
            })!;
            var s = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return s;
        }
        catch { return ""; }
    }

    // ---------- action center ----------

    private FrameworkElement BuildActionCenter()
    {
        var root = new StackPanel();

        // Clock + date
        _bigTime = new TextBlock { Text = DateTime.Now.ToString("HH:mm:ss"), FontSize = 34, FontWeight = FontWeights.Light };
        root.Children.Add(_bigTime);
        root.Children.Add(new TextBlock { Text = DateTime.Now.ToString("dddd, d. MMMM yyyy", De), FontSize = 12, Foreground = Dim, Margin = new Thickness(2, 0, 0, 14) });

        // Quick tiles (they open the matching Windows setting)
        var tiles = new UniformGrid3();
        void Tile(string glyph, string label, Action act)
        {
            var b = new Button { Style = (Style)FindResource("Tile") };
            var sp = new StackPanel();
            sp.Children.Add(Glyph(glyph, 16));
            sp.Children.Add(new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            b.Content = sp;
            b.Click += (_, _) => act();
            tiles.Children.Add(b);
        }
        Tile("", "Netzwerk", () => Toggle(FlyoutKind.Network));
        Tile("", "Bluetooth", () => Run("ms-settings:bluetooth"));
        Tile("", "Nachtlicht", () => Run("ms-settings:nightlight"));
        Tile("", "Nicht stören", () => Run("ms-settings:notifications"));
        Tile("", "Energie", () => Run("ms-settings:powersleep"));
        Tile("", "Einstellungen", () => Run("ms-settings:"));
        root.Children.Add(tiles);

        var vol = VolumeRow();
        vol.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(vol);
        if (MediaCard() is { } media) root.Children.Add(media);

        // Notifications
        root.Children.Add(new TextBlock { Text = "Benachrichtigungen", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 16, 0, 6) });
        var notes = new StackPanel();
        root.Children.Add(notes);
        LoadNotifications(notes);

        root.Children.Add(RunningAppsSection());
        root.Children.Add(Calendar());
        return root;
    }

    private async void LoadNotifications(StackPanel panel)
    {
        try
        {
            var listener = global::Windows.UI.Notifications.Management.UserNotificationListener.Current;
            var access = await listener.RequestAccessAsync();
            if (access != global::Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed) throw new InvalidOperationException();
            var list = await listener.GetNotificationsAsync(global::Windows.UI.Notifications.NotificationKinds.Toast);
            foreach (var n in list.OrderByDescending(n => n.CreationTime).Take(3))
            {
                var binding = n.Notification.Visual.GetBinding(global::Windows.UI.Notifications.KnownNotificationBindings.ToastGeneric);
                var texts = binding?.GetTextElements().Select(t => t.Text).ToList() ?? new List<string>();
                var card = new Border { Background = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255)), CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 2, 0, 2) };
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = n.AppInfo.DisplayInfo.DisplayName, FontSize = 10, Foreground = Dim });
                if (texts.Count > 0) sp.Children.Add(new TextBlock { Text = texts[0], FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                if (texts.Count > 1) sp.Children.Add(new TextBlock { Text = texts[1], FontSize = 11, Foreground = Dim, TextTrimming = TextTrimming.CharacterEllipsis });
                card.Child = sp;
                panel.Children.Add(card);
            }
            if (list.Count == 0) panel.Children.Add(new TextBlock { Text = "Keine neuen Benachrichtigungen", FontSize = 12, Foreground = Dim, Margin = new Thickness(2, 0, 0, 0) });
        }
        catch
        {
            // Windows only lets packaged apps read notifications; fall back to its own list.
            var b = LinkButton("Windows-Benachrichtigungen öffnen", "");
            b.Margin = new Thickness(0);
            b.Click += (_, _) => { CloseFlyout(); Win32.Keys(Win32.VK_LWIN, (byte)'N'); };
            panel.Children.Add(b);
        }
    }

    // ---------- running apps ----------

    private sealed record RunningApp(string Path, string Name, int[] Pids, long Bytes, bool HasWindow);

    /// <summary>All user apps that are running (with or without a window), grouped by program, each with an end button.</summary>
    private FrameworkElement RunningAppsSection()
    {
        var root = new StackPanel();
        var header = new TextBlock { Text = "Laufende Apps", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 16, 0, 6) };
        root.Children.Add(header);
        var list = new StackPanel();
        // Own scroll area so a long list doesn't push the calendar out of reach.
        var scroll = new ScrollViewer { Content = list, MaxHeight = 250, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Focusable = false };
        root.Children.Add(scroll);
        list.Children.Add(new TextBlock { Text = "Wird geladen…", FontSize = 12, Foreground = Dim, Margin = new Thickness(2, 0, 0, 0) });
        LoadRunningApps(list, header, scroll);
        return root;
    }

    private async void LoadRunningApps(StackPanel list, TextBlock header, ScrollViewer scroll)
    {
        var apps = await Task.Run(CollectRunningApps);
        header.Text = $"Laufende Apps ({apps.Count})";
        list.Children.Clear();
        foreach (var app in apps) list.Children.Add(RunningAppRow(app, list, header));
        scroll.UpdateLayout();
        scroll.ScrollToTop(); // background apps are listed first
    }

    private static List<RunningApp> CollectRunningApps()
    {
        int session = Process.GetCurrentProcess().SessionId, self = Environment.ProcessId;
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var withWindow = new HashSet<uint>();
        Win32.EnumWindows((h, _) =>
        {
            if (Win32.IsWindowVisible(h) && Win32.GetWindowTextLength(h) > 0) { Win32.GetWindowThreadProcessId(h, out var pid); withWindow.Add(pid); }
            return true;
        }, IntPtr.Zero);

        var groups = new Dictionary<string, (string Name, List<int> Pids, long Bytes, bool Window)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.SessionId != session || p.Id == self) continue;
                var path = TaskbarApps.PathOf((uint)p.Id);
                // Skip Windows' own components; they are not "apps" and ending them can break the system.
                if (path == null || path.StartsWith(windir, StringComparison.OrdinalIgnoreCase)) continue;
                if (!groups.TryGetValue(path, out var g))
                {
                    var desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
                    g = (string.IsNullOrWhiteSpace(desc) ? System.IO.Path.GetFileNameWithoutExtension(path) : desc.Trim(), new List<int>(), 0, false);
                }
                g.Pids.Add(p.Id);
                g.Bytes += p.WorkingSet64;
                g.Window |= withWindow.Contains((uint)p.Id);
                groups[path] = g;
            }
            catch { }
            finally { p.Dispose(); }
        }
        // Background apps (no window) first, then by memory use.
        return groups.Select(kv => new RunningApp(kv.Key, kv.Value.Name, kv.Value.Pids.ToArray(), kv.Value.Bytes, kv.Value.Window))
                     .OrderBy(a => a.HasWindow).ThenByDescending(a => a.Bytes).ToList();
    }

    private FrameworkElement RunningAppRow(RunningApp app, StackPanel list, TextBlock header)
    {
        var row = new Border { Background = new SolidColorBrush(Color.FromArgb(0x10, 255, 255, 255)), CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 6, 6, 6), Margin = new Thickness(0, 2, 0, 2) };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Image { Width = 20, Height = 20, Source = TaskbarApps.IconFor(app.Path, 0, 32), VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        g.Children.Add(icon);

        var text = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = app.Name, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Text = (app.HasWindow ? "Geöffnet" : "Im Hintergrund") + $" · {app.Bytes / 1048576.0:0} MB" + (app.Pids.Length > 1 ? $" · {app.Pids.Length} Prozesse" : ""),
            FontSize = 10, Foreground = Dim,
        });
        Grid.SetColumn(text, 1);
        g.Children.Add(text);

        var end = new Button { Style = (Style)FindResource("IconButton"), Content = "", FontSize = 11, Padding = new Thickness(8, 6, 8, 6), ToolTip = "Beenden", VerticalAlignment = VerticalAlignment.Center };
        end.Click += async (_, _) =>
        {
            end.IsEnabled = false;
            await Task.Run(() =>
            {
                foreach (var pid in app.Pids)
                    try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
            });
            row.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Theme.Ms(150)));
            await Task.Delay(160);
            list.Children.Remove(row);
            header.Text = $"Laufende Apps ({list.Children.Count})";
        };
        Grid.SetColumn(end, 2);
        g.Children.Add(end);
        row.Child = g;
        return row;
    }

    private FrameworkElement Calendar()
    {
        var today = DateTime.Today;
        var root = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        root.Children.Add(new TextBlock { Text = today.ToString("MMMM yyyy", De), FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 6) });
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 7 };
        foreach (var d in new[] { "Mo", "Di", "Mi", "Do", "Fr", "Sa", "So" })
            grid.Children.Add(new TextBlock { Text = d, FontSize = 10, Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) });
        var first = new DateTime(today.Year, today.Month, 1);
        int offset = ((int)first.DayOfWeek + 6) % 7;
        for (int i = 0; i < offset; i++) grid.Children.Add(new Border());
        for (int day = 1; day <= DateTime.DaysInMonth(today.Year, today.Month); day++)
        {
            bool isToday = day == today.Day;
            grid.Children.Add(new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Margin = new Thickness(1),
                Background = isToday ? new SolidColorBrush(Theme.Accent) : Brushes.Transparent,
                Child = new TextBlock { Text = day.ToString(), FontSize = 11, FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
        }
        root.Children.Add(grid);
        return root;
    }
}

/// <summary>Three equal columns of tiles.</summary>
public class UniformGrid3 : System.Windows.Controls.Primitives.UniformGrid
{
    public UniformGrid3() { Columns = 3; Margin = new Thickness(-4, 0, -4, 0); }
}
