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
    private static readonly IEasingFunction FadeEase = new QuadraticEase { EasingMode = EasingMode.EaseOut };

    private readonly SystemMonitor _sys = SystemMonitor.Instance;
    private readonly MediaService _media = MediaService.Instance;
    private readonly DispatcherTimer _collapseTimer;
    private readonly IslandSurface _surface = new();
    private bool _expanded, _hovering, _menuOpen;
    private int _tick;

    public IslandWindow()
    {
        InitializeComponent();
        Native.MakeOverlay(this, desktopLevel: false);
        Loaded += (_, _) => { ApplyLook(); _surface.SetVisible(true); _surface.Raise(); Native.BringTopmost(this); };

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) }; // short grace period against accidental mouse-outs
        _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); if (!_hovering && !_menuOpen && !Settings.Current.AlwaysExpanded) SetExpanded(false); };

        _sys.Updated += CheckHeat;
        _media.Updated += UpdateMedia;
        TaskbarWindow.FullscreenChanged += full => { if (full) Hide(); else Show(); _surface.SetVisible(!full); };
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
        // Choreography: the body moves on the GPU at once; the content only fades, a beat later when opening
        // and first thing when closing, so it never has to follow the moving edge.
        double k = animate ? Theme.Speed : 0;
        UpdateSurface(animate && k > 0);
        if (expand) SizeContent();
        var hide = new DoubleAnimation(0, TimeSpan.FromMilliseconds(90 * k)) { EasingFunction = FadeEase };
        if (expand)
        {
            Compact.BeginAnimation(OpacityProperty, hide);
            Animate(Expanded, OpacityProperty, 1, TimeSpan.FromMilliseconds(190 * k), FadeEase, 80 * k);
        }
        else
        {
            hide.Completed += (_, _) => { if (!_expanded) SizeContent(); };
            Expanded.BeginAnimation(OpacityProperty, hide);
            Animate(Compact, OpacityProperty, 1, TimeSpan.FromMilliseconds(160 * k), FadeEase, 110 * k);
        }
        Expanded.IsHitTestVisible = expand;
        ShowEqualizer();
        // The open page (player or settings) is kept, so hovering again continues where the user left off.
        if (expand) VolSlider.Refresh();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).OpenSettings();

    /// <summary>The content layer is only as large as the island: it receives the mouse and clips the content.</summary>
    private void SizeContent()
    {
        Island.Width = _expanded ? ExpandedW : CompactW;
        Island.Height = _expanded ? ExpandedH : CompactH;
    }

    private void UpdateSurface(bool animate)
    {
        var s = Settings.Current;
        double k = VisualTreeHelper.GetDpi(this).DpiScaleX * s.IslandScale;
        double radius = (_expanded ? 28 : CompactRadius) * s.IslandCorners;
        double height = _expanded ? ExpandedH : CompactH;
        // Docked: the upper corners sit above the screen edge, so only the lower ones show.
        bool docked = s.IslandShape == 1;
        _surface.MoveTo((float)((_expanded ? ExpandedW : CompactW) * k), (float)((height + (docked ? radius : 0)) * k), (float)((docked ? -radius : 8) * k), (float)(radius * k), _expanded, animate);
    }

    private void ShowEqualizer()
    {
        var s = Settings.Current;
        double k = VisualTreeHelper.GetDpi(this).DpiScaleX * s.IslandScale;
        double corner = s.IslandShape == 1 ? CompactRadius * s.IslandCorners : 0;
        _surface.SetEqualizer(_media.IsPlaying, !_expanded, (float)k, (float)((CompactW - 36) * k), (float)((corner + (CompactH - 14) / 2) * k));
    }

    private void ApplyColor()
    {
        var s = Settings.Current;
        var tint = Settings.ToColor(_heatText != null ? "#5A1010" : s.IslandColor, 0xFF, Color.FromRgb(0x06, 0x06, 0x08));
        _surface.SetLook(tint, s.IslandOpacity, s.RimStyle >= 2 ? Theme.Accent : null);
    }

    /// <summary>Color, size and shape from the design settings.</summary>
    public void ApplyLook()
    {
        var s = Settings.Current;
        ApplyColor();
        // Form: 0 floating, 1 docked to the top edge, 2 capsule (narrower and taller, round cover)
        Island.Margin = new Thickness(0, s.IslandShape == 1 ? 0 : 8, 0, 0);
        Compact.Height = CompactH;
        CompactArt.Width = CompactArt.Height = Capsule ? 28 : 22;
        CompactArt.CornerRadius = new CornerRadius(Capsule ? 14 : 6);
        SizeContent();
        IslandRoot.LayoutTransform = new ScaleTransform(s.IslandScale, s.IslandScale);
        Width = 560 * s.IslandScale;
        Height = 210 * s.IslandScale;
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _surface.Place((int)(Left * dpi), 0, (int)(Width * dpi), (int)(Height * dpi));
        UpdateSurface(animate: false);
        ShowEqualizer();
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
        ShowEqualizer();
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
        _surface.SetGlowColor(color);
        if (mode == 0) { _glowTimer.Stop(); _surface.SetGlow(0); }
        else _glowTimer.Start();
    }

    private void GlowTick()
    {
        // Falls off gently, so it pulses with the beat instead of flickering.
        double peak = IsVisible && _media.IsPlaying ? Volume.Peak() : 0;
        _glowLevel = Math.Max(peak, _glowLevel * 0.82);
        _surface.SetGlow((float)Math.Min(0.9, _glowLevel * 1.3));
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
        if (_expanded) UpdateProgress();
        if (!_media.HasSession && _heatText == null) CompactTitle.Text = DateTime.Now.ToString("HH:mm");
        if (_tick % 8 == 0) { _surface.Raise(); Native.BringTopmost(this); }
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
