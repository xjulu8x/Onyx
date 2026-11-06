using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DesktopOverlay;

public partial class IslandWindow : Window
{
    private const double ExpandedW = 400, ExpandedH = 160;
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

        _sys.Updated += CheckHeat;
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
    }

    // ---------- expand / collapse ----------

    public void ForceExpand() => SetExpanded(true, animate: false);
    public void ApplyAlwaysExpanded() => SetExpanded(Settings.Current.AlwaysExpanded || _hovering);

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

    private void Settings_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).OpenSettings();

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

    /// <summary>A new version turns the gear blue.</summary>
    private static void Animate(IAnimatable el, DependencyProperty prop, double to, TimeSpan dur, IEasingFunction ease, double delayMs = 0) =>
        el.BeginAnimation(prop, new DoubleAnimation(to, dur) { EasingFunction = ease, BeginTime = TimeSpan.FromMilliseconds(delayMs) });

    private void ShowUpdateState() =>
        GearBtn.Foreground = UpdateService.Available != null ? Theme.AccentBrush : Brushes.White;

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

    // ---------- right click ----------

    private void Island_RightClick(object sender, MouseButtonEventArgs e)
    {
        var app = (App)Application.Current;
        var menu = new ContextMenu { PlacementTarget = Island, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var settings = new MenuItem { Header = "Einstellungen", Icon = "E713" };
        settings.Click += (_, _) => app.OpenSettings();
        menu.Items.Add(settings);
        var quit = new MenuItem { Header = "Beenden", Icon = "E7E8" };
        quit.Click += (_, _) => app.Quit();
        menu.Items.Add(quit);
        _menuOpen = true;
        menu.Closed += (_, _) => { _menuOpen = false; if (!_hovering && !Settings.Current.AlwaysExpanded) SetExpanded(false); };
        menu.IsOpen = true;
        e.Handled = true;
    }
}
