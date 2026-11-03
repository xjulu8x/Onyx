using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DesktopOverlay;

public partial class IslandWindow : Window
{
    private const double CompactW = 240, CompactH = 34, ExpandedW = 480, ExpandedH = 160;
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };
    // Opening gets a gentle spring overshoot, closing a soft ease-in-out, so neither feels abrupt.
    private static readonly IEasingFunction OpenEase = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.18 };
    // Closing starts quickly and settles softly (ease-out), so it feels responsive without snapping shut.
    private static readonly IEasingFunction CloseEase = new QuarticEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction FadeEase = new QuadraticEase { EasingMode = EasingMode.EaseOut };

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(IslandWindow),
        new PropertyMetadata(17.0, (d, e) => ((IslandWindow)d).Island.CornerRadius = new CornerRadius((double)e.NewValue)));

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

        ApplyColor();
        if (Settings.Current.AlwaysExpanded) SetExpanded(true, animate: false);
        UpdateMedia();
        UpdateStats();
    }

    // ---------- expand / collapse ----------

    public void ForceExpand() => SetExpanded(true, animate: false);

    private void SetExpanded(bool expand, bool animate = true)
    {
        _expanded = expand;
        double k = animate ? 1 : 0;
        var size = TimeSpan.FromMilliseconds((expand ? 460 : 300) * k);
        var ease = expand ? OpenEase : CloseEase;

        Animate(Island, WidthProperty, expand ? ExpandedW : CompactW, size, ease);
        Animate(Island, HeightProperty, expand ? ExpandedH : CompactH, size, ease);
        Animate(this, RadiusProperty, expand ? 28 : 17, size, ease);

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
        var dur = TimeSpan.FromMilliseconds(animate ? 200 : 0);
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
            arrange.Background = WidgetMover.Editing ? new SolidColorBrush(Color.FromRgb(0x2B, 0x8C, 0xFF)) : new SolidColorBrush(Color.FromArgb(0x1A, 255, 255, 255));
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
        if (SystemMonitor.CpuTempProblem != null)
        {
            var admin = Pill(SystemMonitor.IsElevated ? "CPU-Treiber installieren" : "Als Admin starten", "", app.FixCpuTemp);
            admin.ToolTip = "Wird für die CPU-Temperatur benötigt";
            admin.Margin = new Thickness(0, 0, 6, 0);
            right.Children.Insert(0, admin);
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
            else { s.WidgetColor = hex; app.ApplyWidgetColor(); }
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

    private void ApplyColor() =>
        Island.Background = new SolidColorBrush(Settings.ToColor(Settings.Current.IslandColor, 0xF7, Color.FromRgb(0x06, 0x06, 0x08)));

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
        CompactTitle.Text = has ? _media.Title : DateTime.Now.ToString("HH:mm");
        PlayBtn.Content = _media.IsPlaying ? "" : "";

        var art = has ? _media.Art : null;
        Art.Background = art != null ? new ImageBrush(art) { Stretch = Stretch.UniformToFill } : new SolidColorBrush(Color.FromArgb(0x1F, 255, 255, 255));
        CompactArt.Background = art != null ? new ImageBrush(art) { Stretch = Stretch.UniformToFill } : new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255));
        ArtIcon.Visibility = CompactArtIcon.Visibility = art == null ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress();
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
    }

    private void FastTick()
    {
        _tick++;
        // Idle (collapsed, nothing playing): skip the per-frame work and only keep the clock fresh.
        bool idle = !_expanded && !_media.IsPlaying;
        if (!idle || _tick % 4 == 0) UpdateProgress();
        if (_media.IsPlaying || _tick % 4 == 0) AnimateEq();
        if (!_media.HasSession) CompactTitle.Text = DateTime.Now.ToString("HH:mm");
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
