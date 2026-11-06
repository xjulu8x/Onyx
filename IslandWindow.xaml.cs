using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DesktopOverlay;

public partial class IslandWindow : Window
{
    private const double ExpandedW = 480, ExpandedH = 160;
    private static bool Capsule => Settings.Current.IslandShape == 2;
    private static double CompactW => Capsule ? 180 : 240;
    private static double CompactH => Capsule ? 40 : 34;
    private static double CompactRadius => CompactH / 2;
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };
    // Closing starts quickly and settles softly (ease-out), so it feels responsive without snapping shut.
    private static readonly IEasingFunction CloseEase = new QuarticEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction FadeEase = new QuadraticEase { EasingMode = EasingMode.EaseOut };

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(IslandWindow),
        new PropertyMetadata(17.0, (d, e) => ((IslandWindow)d).SetCorners((double)e.NewValue)));

    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    private readonly SystemMonitor _sys = SystemMonitor.Instance;
    private readonly MediaService _media = MediaService.Instance;
    private readonly DispatcherTimer _collapseTimer;
    private readonly Random _rnd = new();
    private bool _expanded, _hovering, _menuOpen;
    private int _tick;

    public IslandWindow()
    {
        InitializeComponent();
        Native.MakeOverlay(this, desktopLevel: false);
        Loaded += (_, _) => Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) }; // short grace period against accidental mouse-outs
        _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); if (!_hovering && !_menuOpen && !Settings.Current.AlwaysExpanded) SetExpanded(false); };

        _sys.Updated += UpdateStats;
        _media.Updated += UpdateMedia;
        TaskbarWindow.FullscreenChanged += full => { if (full) Hide(); else Show(); };
        VolSlider.Changed += (v, muted) =>
        {
            VolText.Text = $"{v * 100:0}%";
            MuteBtn.Content = VolumeSlider.Glyph(v, muted);
        };

        var fast = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        fast.Tick += (_, _) => FastTick();
        fast.Start();

        UpdateService.Changed += () => Dispatcher.BeginInvoke(ShowUpdateState);
        _glowTimer.Tick += (_, _) => GlowTick();
        ApplyLook();
        if (Settings.Current.AlwaysExpanded) SetExpanded(true, animate: false);
        UpdateMedia();
        UpdateStats();
    }

    // ---------- expand / collapse ----------

    public void ForceExpand() => SetExpanded(true, animate: false);

    private void SetExpanded(bool expand, bool animate = true)
    {
        _expanded = expand;
        double k = animate ? Theme.Speed * Theme.MotionLength : 0;
        var size = TimeSpan.FromMilliseconds((expand ? 460 : 300) * k);
        var ease = expand ? Theme.OpenEase : CloseEase;

        Animate(Island, WidthProperty, expand ? ExpandedW : CompactW, size, ease);
        Animate(Island, HeightProperty, expand ? ExpandedH : CompactH, size, ease);
        Animate(this, RadiusProperty, (expand ? 28 : CompactRadius) * Settings.Current.IslandCorners, size, ease);

        // The content sits at its final position from the start and is revealed by the growing island (clipped),
        // with only a short fade. No per-frame scaling: that re-renders the whole transparent window and stutters.
        Animate(Expanded, OpacityProperty, expand ? 1 : 0, TimeSpan.FromMilliseconds((expand ? 200 : 110) * k), FadeEase);
        Animate(Compact, OpacityProperty, expand ? 0 : 1, TimeSpan.FromMilliseconds((expand ? 140 : 220) * k), FadeEase, expand ? 0 : 130 * k);
        Expanded.IsHitTestVisible = expand;
        // The open page (player or settings) is kept, so hovering again continues where the user left off.
        if (expand) VolSlider.Refresh();
    }

    // ---------- settings page ----------

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings(true);

    private void ShowSettings(bool show, bool animate = true)
    {
        if (show) BuildSettings();
        var from = show ? (FrameworkElement)HomeView : SettingsView;
        var to = show ? (FrameworkElement)SettingsView : HomeView;
        if (to.Visibility == Visibility.Visible && from.Visibility != Visibility.Visible) return;
        var dur = TimeSpan.FromMilliseconds(animate ? 200 * Theme.Speed : 0);
        var fadeOut = new DoubleAnimation(0, dur) { EasingFunction = FadeEase };
        fadeOut.Completed += (_, _) => { if (to.Visibility == Visibility.Visible) from.Visibility = Visibility.Collapsed; };
        from.BeginAnimation(OpacityProperty, fadeOut);
        to.Visibility = Visibility.Visible;
        to.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = FadeEase, BeginTime = TimeSpan.FromMilliseconds(animate ? 90 : 0) });
    }

    private void BuildSettings()
    {
        var s = Settings.Current;
        var app = (App)Application.Current;
        SettingsView.Children.Clear();
        SettingsView.RowDefinitions.Clear();
        SettingsView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SettingsView.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        SettingsView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header: back, title, quit
        var header = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        var back = new Button { Style = (Style)FindResource("IconButton"), Content = "", FontSize = 12, Padding = new Thickness(8, 5, 8, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-8, 0, 0, 0) };
        back.Click += (_, _) => ShowSettings(false);
        header.Children.Add(back);
        header.Children.Add(new TextBlock { Text = "Einstellungen", FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(26, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var quit = Pill("Beenden", "", () => app.Quit());
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(quit);
        header.Children.Add(right);
        SettingsView.Children.Add(header);

        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(grid, 1);
        void Option(string label, bool value, Action<bool> onChange)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 14, 0), Height = 26 };
            row.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var sw = new ToggleSwitch(value) { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            sw.Toggled += v => { onChange(v); s.Save(); };
            row.Children.Add(sw);
            grid.Children.Add(row);
        }
        Option("Immer ausgeklappt", s.AlwaysExpanded, v => s.AlwaysExpanded = v);
        Option("Desktop-Widgets", s.ShowWidgets, v => { s.ShowWidgets = v; app.ApplyWidgetVisibility(); });
        Option("Taskleiste einklappen", s.TaskbarAutoHide, v => { s.TaskbarAutoHide = v; app.Taskbar.ApplyAutoHide(); });
        Option("Mit Windows starten", Settings.Autostart, v => Settings.Autostart = v);

        // Arrange mode for the desktop widgets: the button turns into a blue "Fertig" while it is on.
        Button arrange = null;
        void UpdateArrange()
        {
            ((TextBlock)((StackPanel)arrange.Content).Children[1]).Text = WidgetMover.Editing ? "Fertig" : "Widgets anordnen";
            ((TextBlock)((StackPanel)arrange.Content).Children[0]).Text = WidgetMover.Editing ? "" : "";
            arrange.Background = WidgetMover.Editing ? new SolidColorBrush(Theme.Accent) : new SolidColorBrush(Color.FromArgb(0x1A, 255, 255, 255));
        }
        arrange = Pill("", "", () =>
        {
            if (!s.ShowWidgets) { s.ShowWidgets = true; s.Save(); app.ApplyWidgetVisibility(); }
            WidgetMover.SetEditing(!WidgetMover.Editing);
            UpdateArrange();
        });
        arrange.HorizontalAlignment = HorizontalAlignment.Left;
        arrange.VerticalAlignment = VerticalAlignment.Center;
        UpdateArrange();
        grid.Children.Add(arrange);

        var design = Pill("Design", "", () => app.Flyout.Toggle(FlyoutKind.Design));
        design.HorizontalAlignment = HorizontalAlignment.Left;
        design.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(design);
        if (SystemMonitor.CpuTempProblem != null)
        {
            var admin = Pill(SystemMonitor.IsElevated ? "CPU-Treiber installieren" : "Als Admin starten", "", app.FixCpuTemp);
            admin.ToolTip = "Wird für die CPU-Temperatur benötigt";
            admin.Margin = new Thickness(0, 0, 6, 0);
            right.Children.Insert(0, admin);
        }
        _updatePill = null;
        if (UpdateService.Available != null)
        {
            _updatePill = Pill("", "", app.InstallUpdate);
            _updatePill.Margin = new Thickness(0, 0, 6, 0);
            _updatePill.Background = new SolidColorBrush(Theme.Accent);
            right.Children.Insert(0, _updatePill);
            ShowUpdateState();
        }
        SettingsView.Children.Add(grid);

        var colors = ColorRow();
        Grid.SetRow(colors, 2);
        SettingsView.Children.Add(colors);
    }

    private int _colorTarget; // 0 = island, 1 = taskbar, 2 = widgets

    // Dark tones for the backgrounds (text stays readable on all of them), light tones for the widget text.
    // Few neutral, high-contrast tones. Backgrounds stay dark so the white text on them remains readable.
    private static readonly string[] BgColors = { "#050506", "#0B1A36", "#1E1F23", "#383A40" };      // black, dark blue, anthracite, grey
    private static readonly string[] TextColors = { "#FFFFFF", "#B9BCC4", "#7FA6F0", "#0A0A0C" };    // white, grey, blue, black
    private static readonly Brush SwatchSelected = Brushes.White;
    private static readonly Brush SwatchNormal = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255));

    /// <summary>One compact row: pick what to color (Island / Leiste / Widgets) on the left, the color on the right.</summary>
    private FrameworkElement ColorRow()
    {
        var s = Settings.Current;
        var app = (App)Application.Current;
        var row = new Grid { Height = 28, Margin = new Thickness(0, 2, 0, 0) };
        var targets = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(targets);
        row.Children.Add(swatches);

        string Current() => _colorTarget switch { 0 => s.IslandColor, 1 => s.TaskbarColor, _ => s.WidgetColor };
        void Pick(string hex)
        {
            if (_colorTarget == 0) { s.IslandColor = hex; ApplyColor(); }
            else if (_colorTarget == 1) { s.TaskbarColor = hex; app.Taskbar.ApplyLook(); }
            else { s.WidgetColor = hex; app.ApplyWidgetLook(); }
            s.Save();
        }

        void FillSwatches()
        {
            swatches.Children.Clear();
            foreach (var hex in _colorTarget == 2 ? TextColors : BgColors)
            {
                var sw = new Border
                {
                    Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand, Tag = hex,
                    Background = new SolidColorBrush(Settings.ToColor(hex, 0xFF, Colors.Black)), BorderThickness = new Thickness(2),
                    BorderBrush = string.Equals(hex, Current(), StringComparison.OrdinalIgnoreCase) ? SwatchSelected : SwatchNormal,
                };
                sw.MouseLeftButtonUp += (_, e) =>
                {
                    foreach (Border b in swatches.Children) b.BorderBrush = (string)b.Tag == hex ? SwatchSelected : SwatchNormal;
                    Pick(hex);
                    e.Handled = true;
                };
                swatches.Children.Add(sw);
            }
        }

        var chipOn = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255));
        string[] names = { "Island", "Leiste", "Widgets" };
        for (int i = 0; i < names.Length; i++)
        {
            int index = i;
            var chip = new Border
            {
                CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 4, 0), Cursor = Cursors.Hand,
                Background = i == _colorTarget ? chipOn : Brushes.Transparent,
                Child = new TextBlock { Text = names[i], FontSize = 11, FontWeight = FontWeights.SemiBold },
            };
            chip.MouseLeftButtonUp += (_, e) =>
            {
                _colorTarget = index;
                foreach (Border c in targets.Children) c.Background = c == chip ? chipOn : Brushes.Transparent;
                FillSwatches();
                e.Handled = true;
            };
            targets.Children.Add(chip);
        }
        FillSwatches();
        return row;
    }

    private void ApplyColor()
    {
        Island.Background = Theme.Surface(_heatText != null ? "#5A1010" : Settings.Current.IslandColor, (byte)Math.Round(255 * Settings.Current.IslandOpacity));
        EarLeft.Fill = EarRight.Fill = Island.Background;
        var (rim, width) = Theme.Rim(subtle: false);
        Island.BorderBrush = rim;
        Island.BorderThickness = new Thickness(width);
    }

    private void SetCorners(double r) =>
        Island.CornerRadius = Settings.Current.IslandShape == 1 ? new CornerRadius(0, 0, r, r) : new CornerRadius(r);

    /// <summary>Color, size and rounding from the design settings. The scale is static, so the expand animation stays as cheap as before.</summary>
    public void ApplyLook()
    {
        var s = Settings.Current;
        ApplyColor();
        // Form: 0 floating, 1 docked to the top edge, 2 capsule (narrower and taller, round cover)
        bool docked = s.IslandShape == 1;
        Island.Margin = new Thickness(0, docked ? 0 : 8, 0, 0);
        EarLeft.Visibility = EarRight.Visibility = docked ? Visibility.Visible : Visibility.Collapsed;
        EarLeft.Fill = EarRight.Fill = Island.Background;
        Compact.Height = CompactH;
        CompactArt.Width = CompactArt.Height = Capsule ? 28 : 22;
        CompactArt.CornerRadius = new CornerRadius(Capsule ? 14 : 6);
        if (!_expanded)
        {
            Island.BeginAnimation(WidthProperty, null);
            Island.BeginAnimation(HeightProperty, null);
            Island.Width = CompactW;
            Island.Height = CompactH;
        }
        IslandRoot.LayoutTransform = new ScaleTransform(s.IslandScale, s.IslandScale);
        Width = 560 * s.IslandScale;
        Height = 210 * s.IslandScale;
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        BeginAnimation(RadiusProperty, null);
        Radius = (_expanded ? 28 : CompactRadius) * s.IslandCorners;
        ApplyGlow();
    }

    private Button _updatePill;

    /// <summary>A new version turns the gear blue; the button in the settings shows the version or the download progress.</summary>
    private void ShowUpdateState()
    {
        GearBtn.Foreground = UpdateService.Available != null ? new SolidColorBrush(Theme.Accent) : Brushes.White;
        if (_updatePill == null) return;
        ((TextBlock)((StackPanel)_updatePill.Content).Children[1]).Text =
            UpdateService.Progress is { } p ? $"Lädt {p} %" : $"Update auf {UpdateService.Available}";
        _updatePill.IsEnabled = UpdateService.Progress == null;
    }

    private Button Pill(string text, string glyph, Action onClick)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = text, FontFamily = (FontFamily)FindResource("Ui"), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Style = (Style)FindResource("IconButton"), Content = content, Padding = new Thickness(10, 5, 10, 5), Background = new SolidColorBrush(Color.FromArgb(0x1A, 255, 255, 255)) };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static void Animate(IAnimatable el, DependencyProperty prop, double to, TimeSpan dur, IEasingFunction ease, double delayMs = 0) =>
        el.BeginAnimation(prop, new DoubleAnimation(to, dur) { EasingFunction = ease, BeginTime = TimeSpan.FromMilliseconds(delayMs) });

    private void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        _hovering = true;
        _collapseTimer.Stop();
        if (!_expanded) SetExpanded(true);
    }

    private void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        _hovering = false;
        if (Settings.Current.AlwaysExpanded || _menuOpen) return;
        _collapseTimer.Start();
    }

    // ---------- data ----------

    private void UpdateMedia()
    {
        bool has = _media.HasSession;
        TitleText.Text = has ? _media.Title : "Nichts wird abgespielt";
        Artist.Text = has ? _media.Artist : "";
        CompactTitle.Text = _heatText ?? (has ? _media.Title : DateTime.Now.ToString("HH:mm"));
        PlayBtn.Content = _media.IsPlaying ? "" : "";

        var art = has ? _media.Art : null;
        Art.Background = art != null ? new ImageBrush(art) { Stretch = Stretch.UniformToFill } : new SolidColorBrush(Color.FromArgb(0x1F, 255, 255, 255));
        CompactArt.Background = art != null ? new ImageBrush(art) { Stretch = Stretch.UniformToFill } : new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255));
        ArtIcon.Visibility = CompactArtIcon.Visibility = art == null ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(art, _glowArt)) { _glowArt = art; ApplyGlow(); }
        UpdateProgress();
    }

    // ---------- music glow ----------

    private readonly DispatcherTimer _glowTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private object _glowArt;
    private double _glowLevel;

    /// <summary>
    /// A soft light at the lower edge that follows the real audio level. Only the opacity of this one small element changes,
    /// at 20 frames per second: animating more of the transparent window is what made the island stutter before.
    /// </summary>
    public void ApplyGlow()
    {
        int mode = Settings.Current.MusicGlow;
        var color = mode == 2 && _media.Art is System.Windows.Media.Imaging.BitmapSource art ? Theme.CoverColor(art) : Theme.Accent;
        GlowBar.Background = new LinearGradientBrush(Color.FromArgb(0, color.R, color.G, color.B), color, 90);
        if (mode == 0) { _glowTimer.Stop(); GlowBar.Opacity = 0; }
        else _glowTimer.Start();
    }

    private void GlowTick()
    {
        // Falls off gently, so it pulses with the beat instead of flickering.
        double peak = IsVisible && _media.IsPlaying ? Volume.Peak() : 0;
        _glowLevel = Math.Max(peak, _glowLevel * 0.82);
        double opacity = Math.Round(Math.Min(0.9, _glowLevel * 1.3), 2);
        if (opacity != GlowBar.Opacity) GlowBar.Opacity = opacity;
    }

    // ---------- heat alarm ----------

    private string _heatText;

    /// <summary>Above the limit the island turns red and names the hot part. It calms down 3 degrees below, so it doesn't flicker.</summary>
    private void CheckHeat()
    {
        var s = Settings.Current;
        double cpu = _sys.CpuTemp ?? 0, gpu = _sys.GpuTemp ?? 0, hottest = Math.Max(cpu, gpu);
        bool hot = s.HeatAlarm && hottest >= (_heatText != null ? s.HeatLimit - 3 : s.HeatLimit);
        string text = hot ? $"{(cpu >= gpu ? "CPU" : "GPU")} {hottest:0}° – zu heiß" : null;
        if (text == _heatText) return;
        bool changed = (text == null) != (_heatText == null);
        _heatText = text;
        if (changed) ApplyColor();
        UpdateMedia();
    }

    private void UpdateProgress()
    {
        var dur = _media.Duration;
        var pos = _media.Position;
        Progress.Value = dur.TotalSeconds > 0 ? Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0, 1) : 0;
        PosText.Text = MediaService.Fmt(pos);
        DurText.Text = MediaService.Fmt(dur);
    }

    private void UpdateStats()
    {
        CpuText.Text = $"{_sys.Cpu:0}%";
        CpuBar.Value = _sys.Cpu / 100;
        RamText.Text = $"{_sys.RamUsedGB:0.0} / {_sys.RamTotalGB:0.0} GB";
        RamBar.Value = _sys.RamTotalGB > 0 ? _sys.RamUsedGB / _sys.RamTotalGB : 0;
        DownText.Text = SystemMonitor.Rate(_sys.DownBps);
        UpText.Text = SystemMonitor.Rate(_sys.UpBps);
        CheckHeat();
    }

    private void FastTick()
    {
        _tick++;
        // Idle (collapsed, nothing playing): skip the per-frame work and only keep the clock fresh.
        bool idle = !_expanded && !_media.IsPlaying;
        if (!idle || _tick % 4 == 0) UpdateProgress();
        if (_media.IsPlaying || _tick % 4 == 0) AnimateEq();
        if (!_media.HasSession && _heatText == null) CompactTitle.Text = DateTime.Now.ToString("HH:mm");
        if (_tick % 8 == 0) Native.BringTopmost(this);
    }

    private void AnimateEq()
    {
        foreach (var r in new[] { Eq1, Eq2, Eq3, Eq4 })
        {
            double to = _media.IsPlaying ? 0.25 + _rnd.NextDouble() * 0.75 : 0.2;
            ((ScaleTransform)r.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(to, TimeSpan.FromMilliseconds(240)) { EasingFunction = Ease });
        }
    }

    // ---------- media / volume ----------

    private void Prev_Click(object sender, RoutedEventArgs e) => _media.Previous();
    private void Play_Click(object sender, RoutedEventArgs e) => _media.PlayPause();
    private void Next_Click(object sender, RoutedEventArgs e) => _media.Next();
    private void Art_Click(object sender, MouseButtonEventArgs e) => _media.PlayPause();
    private void Mute_Click(object sender, RoutedEventArgs e) { Volume.ToggleMute(); VolSlider.Refresh(); }

    // ---------- settings (right click) ----------

    private void Island_RightClick(object sender, MouseButtonEventArgs e)
    {
        var s = Settings.Current;
        var app = (App)Application.Current;
        var menu = new ContextMenu { PlacementTarget = Island, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };

        void Toggle(string header, bool isChecked, Action<bool> onChange)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
            item.Click += (_, _) => { onChange(item.IsChecked); s.Save(); };
            menu.Items.Add(item);
        }

        Toggle("Island immer ausgeklappt", s.AlwaysExpanded, v => { s.AlwaysExpanded = v; SetExpanded(v || _hovering); });
        Toggle("Desktop-Widgets anzeigen", s.ShowWidgets, v => { s.ShowWidgets = v; app.ApplyWidgetVisibility(); });
        Toggle("Taskleiste automatisch einklappen", s.TaskbarAutoHide, v => { s.TaskbarAutoHide = v; app.Taskbar.ApplyAutoHide(); });
        Toggle("Mit Windows starten", Settings.Autostart, v => Settings.Autostart = v);
        Toggle("Speicher sparen (nach Neustart von Onyx)", s.SaveMemory, v => s.SaveMemory = v);
        if (SystemMonitor.CpuTempProblem != null)
        {
            var admin = new MenuItem { Header = SystemMonitor.IsElevated ? "Treiber für CPU-Temperatur installieren" : "Als Administrator neu starten (CPU-Temperatur)", Icon = "" };
            admin.Click += (_, _) => app.FixCpuTemp();
            menu.Items.Add(admin);
        }
        menu.Items.Add(new Separator());
        var quit = new MenuItem { Header = "Beenden", Icon = "" };
        quit.Click += (_, _) => app.Quit();
        menu.Items.Add(quit);

        _menuOpen = true;
        menu.Closed += (_, _) => { _menuOpen = false; if (!_hovering && !s.AlwaysExpanded) SetExpanded(false); };
        menu.IsOpen = true;
        e.Handled = true;
    }
}
