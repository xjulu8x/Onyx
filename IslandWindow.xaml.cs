using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DesktopOverlay;

public partial class IslandWindow : Window
{
    private const double ExpandedW = 820, ExpandedH = 330, ExpandedRadius = 24;
    private const double WindowW = 900, WindowH = 380; // room for the open panel and its spring
    private static bool Capsule => Settings.Current.IslandShape == 2;
    private double CompactW => (Capsule ? 200 : 260) + (_flash != null ? 70 : 0); // wider while a hint shows
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
        Loaded += (_, _) => { ApplyLook(); _surface.SetVisible(true); KeepOnTop(); };

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
        BuildPanel();
        KeyboardHook.StatusKey += OnStatusKey;
        _glowTimer.Tick += (_, _) => GlowTick();
        ApplyLook();
        if (Settings.Current.AlwaysExpanded) SetExpanded(true, animate: false);
        UpdateMedia();
    }

    // ---------- expand / collapse ----------

    public void ForceExpand() => SetExpanded(true, animate: false);

    /// <summary>Debug aid for --demo: opens and closes by itself, so the motion can be recorded.</summary>
    public async void Demo()
    {
        await Task.Delay(5000);
        for (int i = 0; i < 6; i++) { SetExpanded(i % 2 == 0); await Task.Delay(1000); }
        ((App)Application.Current).Quit();
    }
    public void ApplyAlwaysExpanded() => SetExpanded(Settings.Current.AlwaysExpanded || _hovering);

    private void SetExpanded(bool expand, bool animate = true)
    {
        if (expand == _expanded && animate) return;
        _expanded = expand;
        // Choreography: body, cover and title move on the GPU at once, on critically damped springs that start from
        // where they are, so a quick in-and-out never jumps. The controls materialize out of the notch: a short
        // fade with a slight grow from the top edge (only opacity and a cached scale, so the window stays cheap).
        double k = animate ? Theme.Speed : 0;
        bool moving = animate && k > 0;
        UpdateSurface(moving);
        MoveMedia(moving);
        if (expand) SizeContent();
        var hide = new DoubleAnimation(0, TimeSpan.FromMilliseconds(70 * k)) { EasingFunction = FadeEase };
        double grow = Theme.ReduceMotion ? 1 : 0.96;
        if (expand)
        {
            Compact.BeginAnimation(OpacityProperty, hide);
            Animate(Expanded, OpacityProperty, 1, TimeSpan.FromMilliseconds(150 * k), FadeEase, 40 * k);
            Animate(_contentScale, ScaleTransform.ScaleXProperty, 1, TimeSpan.FromMilliseconds(190 * k), OutEase, 0);
            Animate(_contentScale, ScaleTransform.ScaleYProperty, 1, TimeSpan.FromMilliseconds(190 * k), OutEase, 0);
            if (moving && _contentScale.ScaleX >= 0.999) { _contentScale.ScaleX = _contentScale.ScaleY = grow; Animate(_contentScale, ScaleTransform.ScaleXProperty, 1, TimeSpan.FromMilliseconds(190 * k), OutEase); Animate(_contentScale, ScaleTransform.ScaleYProperty, 1, TimeSpan.FromMilliseconds(190 * k), OutEase); }
        }
        else
        {
            hide.Completed += (_, _) => { if (!_expanded) SizeContent(); };
            Expanded.BeginAnimation(OpacityProperty, hide);
            Animate(_contentScale, ScaleTransform.ScaleXProperty, grow, TimeSpan.FromMilliseconds(90 * k), FadeEase);
            Animate(_contentScale, ScaleTransform.ScaleYProperty, grow, TimeSpan.FromMilliseconds(90 * k), FadeEase);
            Animate(Compact, OpacityProperty, 1, TimeSpan.FromMilliseconds(120 * k), FadeEase, 90 * k);
        }
        Expanded.IsHitTestVisible = expand;
        ShowEqualizer();
        // The open page (player or settings) is kept, so hovering again continues where the user left off.
        if (expand) VolSlider.Refresh();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).OpenSettings();

    // ---------- tabs and pages ----------

    private static readonly IEasingFunction OutEase = new CubicEase { EasingMode = EasingMode.EaseOut };
    private readonly ScaleTransform _contentScale = new(1, 1);
    private static readonly Brush Card = new SolidColorBrush(Color.FromArgb(0x10, 255, 255, 255));
    private static readonly Brush CardHover = new SolidColorBrush(Color.FromArgb(0x1C, 255, 255, 255));
    private static readonly Brush TabOn = new SolidColorBrush(Color.FromArgb(0x24, 255, 255, 255));
    private int _page;

    private void BuildPanel()
    {
        Expanded.RenderTransform = _contentScale;
        Expanded.RenderTransformOrigin = new Point(0.5, 0);

        var tabs = new (string Glyph, string Label)[] { ("", "Start"), ("", "Steuerung"), ("", "Mitteilungen") };
        for (int i = 0; i < tabs.Length; i++)
        {
            int index = i;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = tabs[i].Glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = tabs[i].Label, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var tab = new Border { CornerRadius = new CornerRadius(15), Padding = new Thickness(12, 6, 14, 6), Margin = new Thickness(0, 0, 4, 0), Cursor = Cursors.Hand, Child = content, Background = Brushes.Transparent };
            Press(tab);
            tab.MouseLeftButtonUp += (_, _) => ShowPage(index);
            Tabs.Children.Add(tab);
        }

        // Tools: the result shows as a short hint in the notch.
        void Tool(string glyph, string label, Action run)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 15, Width = 22, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 100 });
            var tile = new Border { Background = Card, CornerRadius = new CornerRadius(14), Margin = new Thickness(4), Padding = new Thickness(12, 0, 8, 0), Cursor = Cursors.Hand, Child = row };
            Hover(tile);
            Press(tile);
            tile.MouseLeftButtonUp += (_, _) => { SetExpanded(false); run(); };
            ToolsGrid.Children.Add(tile);
        }
        ToolsGrid.Margin = new Thickness(-4);
        Tool("", "Bereich", IslandTools.ScreenshotArea);
        Tool("", "Ganzer Bildschirm", () => IslandTools.ScreenshotFull(Flash));
        Tool("", "Text vom Bildschirm", () => IslandTools.ReadText(Flash));
        Tool("", "QR-Code", () => IslandTools.ReadQr(Flash));
        Tool("", "Pipette", () => IslandTools.PickColor(Flash));
        Tool("", "Aufnahme", () => IslandTools.Record(Flash));

        // Control: big tiles instead of the action center tiles and the tray buttons of the old taskbar.
        void Control(string glyph, string label, string detail, Action run)
        {
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            stack.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 18, Margin = new Thickness(0, 0, 0, 10) });
            stack.Children.Add(new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeights.SemiBold });
            stack.Children.Add(new TextBlock { Text = detail, FontSize = 11, Foreground = (Brush)FindResource("Dim"), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            var tile = new Border { Background = Card, CornerRadius = new CornerRadius(16), Margin = new Thickness(4), Padding = new Thickness(14, 12, 14, 12), Cursor = Cursors.Hand, Child = stack };
            Hover(tile);
            Press(tile);
            tile.MouseLeftButtonUp += (_, _) => { SetExpanded(false); run(); };
            ControlTiles.Children.Add(tile);
        }
        var app = (App)Application.Current;
        Control("", "Netzwerk", "WLAN und Verbindung", () => app.Flyout.Toggle(FlyoutKind.Network, fromTop: true));
        Control("", "Bluetooth", "Geräte verbinden", () => IslandTools.Open("ms-settings:bluetooth"));
        Control("", "Sound", "Ausgabe und Eingabe", () => IslandTools.Open("ms-settings:sound"));
        Control("", "Nachtlicht", "Wärmere Farben", () => IslandTools.Open("ms-settings:nightlight"));
        Control("", "Nicht stören", "Mitteilungen", () => IslandTools.Open("ms-settings:notifications"));
        Control("", "Energie", "Akku und Leistung", () => IslandTools.Open("ms-settings:powersleep"));
        Control("", "Task-Manager", "Laufende Prozesse", () => IslandTools.Open("taskmgr.exe"));
        Control("", "Onyx", "Einstellungen", app.OpenSettings);

        Tray.Changed += () => Dispatcher.BeginInvoke(() => { if (_page == 1) FillTray(); });
        ShowPage(0);
    }

    private void ShowPage(int index)
    {
        _page = index;
        for (int i = 0; i < Tabs.Children.Count; i++)
        {
            var tab = (Border)Tabs.Children[i];
            tab.Background = i == index ? TabOn : Brushes.Transparent;
            ((StackPanel)tab.Child).Children[1].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed; // only the open tab shows its name
        }
        StartPage.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        ControlPage.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        NotifyPage.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        // Cover and title live on the start page only.
        _surface.ShowMedia(index == 0 || !_expanded);
        if (index == 1) FillTray();
        // Built when opened and dropped on leaving, so notifications and the app list cost nothing in between.
        NotifyPage.Content = index == 2 ? ((App)Application.Current).Flyout.BuildActivity() : null;
    }

    /// <summary>Debug aid for --snapshot: renders each page of the open panel to a PNG file.</summary>
    public void RenderPages(string dir)
    {
        var hitArea = Island.Background;
        Island.Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0E)); // the body is drawn by the surface, so give the picture one
        for (int i = 0; i < Tabs.Children.Count; i++)
        {
            ShowPage(i);
            UpdateLayout();
            var image = Render(Island, Island.ActualWidth, Island.ActualHeight);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var file = System.IO.File.Create(System.IO.Path.Combine(dir, $"IslandPage{i}.png"));
            png.Save(file);
        }
        Island.Background = hitArea;
        ShowPage(0);
    }

    private void FillTray()
    {
        TrayIcons.Children.Clear();
        foreach (var icon in Tray.Visible()) TrayIcons.Children.Add(Tray.Button(icon));
    }

    private static void Hover(Border tile)
    {
        tile.MouseEnter += (_, _) => tile.Background = CardHover;
        tile.MouseLeave += (_, _) => tile.Background = Card;
    }

    /// <summary>Feedback on press, not on release: a quick, slight shrink that springs back.</summary>
    private static void Press(FrameworkElement element)
    {
        var scale = new ScaleTransform(1, 1);
        element.RenderTransform = scale;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        void To(double value, int ms)
        {
            var a = new DoubleAnimation(value, TimeSpan.FromMilliseconds(ms)) { EasingFunction = OutEase };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
        element.PreviewMouseLeftButtonDown += (_, _) => To(0.96, 90);
        element.PreviewMouseLeftButtonUp += (_, _) => To(1, 160);
        element.MouseLeave += (_, _) => To(1, 160);
    }

    // ---------- hints in the notch ----------

    /// <summary>Caps Lock and the volume keys: the new state shows briefly in the notch. Read a moment later, once Windows applied it.</summary>
    private async void OnStatusKey(int key)
    {
        await Task.Delay(60);
        if (key == 0x14) Flash(Keyboard.IsKeyToggled(Key.CapsLock) ? "Caps Lock an" : "Caps Lock aus");
        else Flash(Volume.IsMuted() ? "Ton aus" : $"Lautstärke {Volume.Get() * 100:0} %");
    }

    private string _flash;
    private DispatcherTimer _flashTimer;

    /// <summary>Shows a short message in the notch, which widens for it, like a live activity.</summary>
    public void Flash(string text)
    {
        _flashTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(1800), DispatcherPriority.Normal, (_, _) => { _flashTimer.Stop(); _flash = null; RefreshCompact(); }, Dispatcher);
        _flashTimer.Stop();
        _flash = text;
        RefreshCompact();
        _flashTimer.Start();
    }

    private void RefreshCompact()
    {
        UpdateMedia();
        Compact.Width = CompactW - 24;
        if (_expanded) return;
        SizeContent();
        UpdateSurface(animate: Theme.Speed > 0);
        MoveMedia(animate: Theme.Speed > 0);
        ShowEqualizer();
    }

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
        double radius = (_expanded ? ExpandedRadius : CompactRadius) * s.IslandCorners;
        double height = _expanded ? ExpandedH : CompactH;
        // Docked: the upper corners sit above the screen edge, so only the lower ones show.
        bool docked = s.IslandShape == 1;
        _surface.MoveTo((float)((_expanded ? ExpandedW : CompactW) * k), (float)((height + (docked ? radius : 0)) * k), (float)((docked ? -radius : 8) * k), (float)(radius * k), _expanded, animate);
    }

    // The content goes on top first, then the body right below it, so the body never covers the content, not even for a moment.
    private void KeepOnTop()
    {
        Native.BringTopmost(this);
        _surface.PlaceBelow(new System.Windows.Interop.WindowInteropHelper(this).Handle);
    }

    // ---------- cover and title on the GPU ----------

    private const double TextScale = 2; // images are rendered sharper than shown
    private string _smallKey, _bigKey;
    private object _artKey = new();

    /// <summary>Renders what changed into images for the surface. Happens per song, not per frame.</summary>
    private void SendMedia()
    {
        if (!_surface.MediaReady) return;
        var font = (FontFamily)FindResource("Ui");
        TextBlock Text(string text, double size, FontWeight weight, double opacity) => new()
        {
            Text = text, FontFamily = font, FontSize = size, FontWeight = weight, Foreground = Brushes.White, Opacity = opacity, TextTrimming = TextTrimming.CharacterEllipsis,
        };

        System.Windows.Media.Imaging.BitmapSource art = null, small = null, big = null;
        object artKey = (object)_media.Art ?? "none";
        if (!artKey.Equals(_artKey))
        {
            _artKey = artKey;
            var cover = new Border { Width = 120, Height = 120, CornerRadius = new CornerRadius(14), Background = _media.HasSession && _media.Art != null ? new ImageBrush(_media.Art) { Stretch = Stretch.UniformToFill } : new SolidColorBrush(Color.FromArgb(0x1F, 255, 255, 255)) };
            if (_media.Art == null) cover.Child = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 30, Foreground = Brushes.White, Opacity = 0.65, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            art = Render(cover, 120, 120);
        }
        string smallKey = CompactTitle.Text + font;
        if (smallKey != _smallKey)
        {
            _smallKey = smallKey;
            small = Render(Text(CompactTitle.Text, 12, FontWeights.SemiBold, 1), CompactW - 24 - 22 - 42, 18);
        }
        string bigKey = TitleText.Text + "|" + Artist.Text + font;
        if (bigKey != _bigKey)
        {
            _bigKey = bigKey;
            var both = new StackPanel();
            both.Children.Add(Text(TitleText.Text, 14, FontWeights.Bold, 1));
            var artist = Text(Artist.Text, 11, FontWeights.SemiBold, 0.65);
            artist.Margin = new Thickness(0, 3, 0, 0);
            both.Children.Add(artist);
            big = Render(both, 284, 40);
        }
        _surface.SetMedia(art, small, big);
        foreach (var hidden in new UIElement[] { Art, CompactArt, CompactTitle, TitleText, Artist }) hidden.Opacity = 0;
        ArtIcon.Visibility = CompactArtIcon.Visibility = Visibility.Collapsed;
    }

    private static System.Windows.Media.Imaging.BitmapSource Render(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(width * TextScale), (int)(height * TextScale), 96 * TextScale, 96 * TextScale, PixelFormats.Pbgra32);
        image.Render(element);
        image.Freeze();
        return image;
    }

    private void MoveMedia(bool animate)
    {
        var s = Settings.Current;
        float k = (float)(VisualTreeHelper.GetDpi(this).DpiScaleX * s.IslandScale);
        float corner = s.IslandShape == 1 ? (float)((_expanded ? ExpandedRadius : CompactRadius) * s.IslandCorners) : 0;
        // Expanded: the music card (tabs 12+34, gap 8, card padding 14), compact: left in the notch.
        float artSize = _expanded ? 120 : Capsule ? 28 : 22;
        float artX = _expanded ? 30 : 12, artY = _expanded ? 68 : (float)((CompactH - artSize) / 2);
        float textX = artX + artSize + (_expanded ? 14 : 9), textY = _expanded ? 86 : (float)((CompactH - 18) / 2);
        _surface.SetTextScale(k / (float)TextScale);
        _surface.MoveMedia(new System.Numerics.Vector2(artX * k, (artY + corner) * k), artSize * k, new System.Numerics.Vector2(textX * k, (textY + corner) * k), _expanded, animate);
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
        Compact.Width = CompactW - 24;
        CompactArt.Width = CompactArt.Height = Capsule ? 28 : 22;
        CompactArt.CornerRadius = new CornerRadius(Capsule ? 14 : 6);
        SizeContent();
        IslandRoot.LayoutTransform = new ScaleTransform(s.IslandScale, s.IslandScale);
        Width = WindowW * s.IslandScale;
        Height = WindowH * s.IslandScale;
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _surface.Place((int)(Left * dpi), 0, (int)(Width * dpi), (int)(Height * dpi));
        UpdateSurface(animate: false);
        _smallKey = _bigKey = null; // size or font may have changed
        _artKey = new object();
        SendMedia();
        MoveMedia(animate: false);
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

    /// <summary>Without music the notch shows the time and, when known, the CPU temperature.</summary>
    private string IdleText() => DateTime.Now.ToString("HH:mm") + (_sys.CpuTemp is { } t ? $"   {t:0}°" : "");

    private void UpdateMedia()
    {
        bool has = _media.HasSession;
        TitleText.Text = has ? _media.Title : "Nichts wird abgespielt";
        Artist.Text = has ? _media.Artist : "";
        CompactTitle.Text = _flash ?? _heatText ?? (has ? _media.Title : IdleText());
        PlayBtn.Content = _media.IsPlaying ? "" : "";
        SourceText.Text = has ? _media.Source.ToUpperInvariant() : "";
        OutputText.Text = Volume.DeviceName() ?? "";

        var art = has ? _media.Art : null;
        Art.Background = art != null ? new ImageBrush(art) { Stretch = Stretch.UniformToFill } : new SolidColorBrush(Color.FromArgb(0x1F, 255, 255, 255));
        CompactArt.Background = art != null ? new ImageBrush(art) { Stretch = Stretch.UniformToFill } : new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255));
        ArtIcon.Visibility = CompactArtIcon.Visibility = art == null ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(art, _glowArt)) { _glowArt = art; ApplyGlow(); }
        SendMedia();
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
        if (!_media.HasSession && _heatText == null && _flash == null && CompactTitle.Text != IdleText()) UpdateMedia();
        if (_tick % 8 == 0) KeepOnTop();
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
