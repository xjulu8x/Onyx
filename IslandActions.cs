using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopOverlay;

/// <summary>Start page of the island: three quick actions (resolution, saturation, crosshair) and the pixel character.</summary>
public partial class IslandWindow
{
    private TextBlock _resValue, _vibValue, _crossValue;
    private Border _crossTile;
    private VolumeSlider _vibSlider;
    private readonly DispatcherTimer _vibSave = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private const int GamePage = 6; // "Spiel" in the settings window

    private void BuildActions()
    {
        (Border Tile, TextBlock Value, StackPanel Body) Tile(string glyph, string label)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), FontFamily = Theme.Mono, FontSize = 9.5, Margin = new Thickness(7, 0, 0, 0), Foreground = Theme.Res("Dim"), VerticalAlignment = VerticalAlignment.Center });
            var value = new TextBlock { FontFamily = Theme.Mono, FontSize = 11.5, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
            var body = new StackPanel();
            body.Children.Add(head);
            body.Children.Add(value);
            var tile = new Border { Background = Card, BorderBrush = Theme.Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), Margin = new Thickness(4), Padding = new Thickness(10, 9, 10, 8), Cursor = Cursors.Hand, Child = body };
            Hover(tile);
            Press(tile);
            ActionsGrid.Children.Add(tile);
            return (tile, value, body);
        }

        // 1: resolution and refresh rate of the main screen.
        var (res, resValue, _) = Tile("", "Anzeige");
        _resValue = resValue;
        res.MouseLeftButtonUp += (_, _) => ResolutionMenu(res);

        // 2: saturation (NVIDIA digital vibrance), dragged right on the tile.
        var (vib, vibValue, vibBody) = Tile("", "Sättigung");
        _vibValue = vibValue;
        vib.Cursor = Cursors.Arrow;
        _vibSlider = new VolumeSlider(() => (float)(Vibrance.Read() ?? 0.5), v =>
        {
            Vibrance.Set(v);
            Settings.Current.VibranceLevel = Math.Round(v, 3);
            _vibValue.Text = $"{v * 100:0} %";
            _vibSave.Stop(); _vibSave.Start();
        }) { Margin = new Thickness(0, 6, 0, 0) };
        vibBody.Children.Add(_vibSlider);
        vib.ToolTip = "Ziehen oder Mausrad · Rechtsklick: Standard";
        vib.MouseRightButtonUp += (_, e) =>
        {
            if (!Vibrance.Available) return;
            Vibrance.Set(Vibrance.Default());
            Settings.Current.VibranceLevel = null; // back to the driver's own value
            Settings.Current.Save();
            RefreshActions();
            e.Handled = true;
        };
        _vibSave.Tick += (_, _) => { _vibSave.Stop(); Settings.Current.Save(); };

        // 3: crosshair overlay on and off; right click opens its settings.
        var (cross, crossValue, _) = Tile("\uE710", "Crosshair");
        _crossValue = crossValue;
        _crossTile = cross;
        cross.ToolTip = "Klick: an/aus · Rechtsklick: Einstellungen";
        cross.MouseLeftButtonUp += (_, _) =>
        {
            Settings.Current.CrosshairOn = !Settings.Current.CrosshairOn;
            Settings.Current.Save();
            ((App)Application.Current).Crosshair.Apply();
            RefreshActions();
        };
        cross.MouseRightButtonUp += (_, e) => { ((App)Application.Current).OpenSettings(GamePage); e.Handled = true; };
    }

    /// <summary>Current values of the three actions and the character; called when the start page shows.</summary>
    private void RefreshActions()
    {
        if (_resValue == null) return;
        var mode = Display.Current();
        _resValue.Text = $"{mode.Width}x{mode.Height}\n{mode.Hz} Hz";
        if (Vibrance.Read() is { } v) { _vibValue.Text = $"{v * 100:0} %"; _vibSlider.Visibility = Visibility.Visible; _vibSlider.Refresh(); }
        else { _vibValue.Text = "NUR NVIDIA"; _vibSlider.Visibility = Visibility.Collapsed; }
        bool on = Settings.Current.CrosshairOn;
        _crossValue.Text = on ? "AN" : "AUS";
        _crossTile.BorderBrush = on ? Theme.AccentBrush : Theme.Res("Line");
        var s = Settings.Current;
        PetTag.Text = $"+ {s.PetName.ToUpperInvariant()} / {PixelPet.Shapes[Math.Clamp(s.PetShape, 0, PixelPet.Shapes.Length - 1)].ToUpperInvariant()}";
        PetMood.Text = _media.IsPlaying && s.PetDance ? "♪ TANZT" : s.PetBehavior == 2 ? "SCHLÄFT" : "CHILLT";
    }

    private void ResolutionMenu(FrameworkElement target)
    {
        var current = Display.Current();
        var menu = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        // One entry per resolution, its refresh rates inside; the common ones of this screen only.
        foreach (var group in Display.Modes().GroupBy(m => (m.Width, m.Height)).Take(14))
        {
            var item = new MenuItem { Header = $"{group.Key.Width} x {group.Key.Height}", IsChecked = group.Key == (current.Width, current.Height) };
            foreach (var mode in group.OrderByDescending(m => m.Hz))
            {
                var hz = new MenuItem { Header = $"{mode.Hz} Hz", IsChecked = mode == current };
                hz.Click += (_, _) =>
                {
                    if (Display.Apply(mode)) Flash($"{mode.Width}x{mode.Height} · {mode.Hz} Hz");
                    else Flash("Modus nicht möglich");
                };
                item.Items.Add(hz);
            }
            menu.Items.Add(item);
        }
        _menuOpen = true;
        menu.Closed += (_, _) => { _menuOpen = false; if (!_hovering && !Settings.Current.AlwaysExpanded) SetExpanded(false); };
        menu.IsOpen = true;
    }

    /// <summary>
    /// The character lives on its own layer above the panel: same fade and grow as the panel, placed over its spot.
    /// So the panel can stay one cached picture (smooth opening) while the character moves.
    /// </summary>
    public string PetDebug() => $"spot {PetSpot.ActualWidth}x{PetSpot.ActualHeight} at {PetSpot.TranslatePoint(new Point(0, 0), Expanded)}; pet {Pet.ActualWidth}x{Pet.ActualHeight} set {Pet.Width}x{Pet.Height} at {Canvas.GetLeft(Pet)},{Canvas.GetTop(Pet)}; layer {PetLayer.ActualWidth}x{PetLayer.ActualHeight} clip {PetLayer.Clip}";

    private void WirePet()
    {
        PetLayer.RenderTransform = _contentScale;
        PetLayer.RenderTransformOrigin = new Point(0.5, 0);
        PetLayer.SetBinding(OpacityProperty, new System.Windows.Data.Binding(nameof(Opacity)) { Source = Expanded });
        PetLayer.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(Visibility)) { Source = StartPage });
        PetLayer.SetBinding(IsHitTestVisibleProperty, new System.Windows.Data.Binding(nameof(IsHitTestVisible)) { Source = Expanded });
        void Place()
        {
            if (PetSpot.ActualWidth <= 0) return;
            var at = PetSpot.TranslatePoint(new Point(0, 0), Expanded);
            Canvas.SetLeft(Pet, at.X);
            Canvas.SetTop(Pet, at.Y);
            Pet.Width = PetSpot.ActualWidth;
            Pet.Height = PetSpot.ActualHeight;
        }
        PetSpot.SizeChanged += (_, _) => Place();
        PetSpot.Loaded += (_, _) => Place();
    }

    /// <summary>The character only moves while it can be seen: island open on the start page.</summary>
    private void ShowPet()
    {
        Pet.Running = _expanded && _page == 0 && _swap?.IsEnabled != true; // starts when the live panel is shown
        UpdateNotchPet();
    }


    /// <summary>Without music the small island shows the character instead of the cover, the time moves to the right.</summary>
    private bool NotchPetOn => Settings.Current.PetInNotch && !_expanded && !_media.HasSession && _heatText == null; // hints show above it, centred

    private bool _notchPetShown;

    private void UpdateNotchPet()
    {
        bool on = NotchPetOn;
        NotchPet.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        NotchPet.Running = on;
        // The text in the middle (time or a hint): the character has to duck under it.
        NotchPet.ObstacleHalfWidth = Math.Max(26, (_flash ?? IdleText()).Length * 3.8);
        if (on == _notchPetShown) return;
        _notchPetShown = on;
        _smallKey = null; // the time moves to the middle (or back), so its picture is made again
        SendMedia();
        _surface.ShowMedia(!on && (!_expanded || _page == 0));
        MoveMedia(animate: false);
        ShowEqualizer();
    }

    /// <summary>Settings of the character changed: both pictures are drawn again.</summary>
    public void PetChanged()
    {
        Pet.InvalidateVisual();
        NotchPet.InvalidateVisual();
        UpdateNotchPet();
        RefreshActions();
    }

    /// <summary>
    /// Draws the vinyl record (black, fine grooves, a soft sheen, a ring around the label) and the tone arm
    /// (pivot with counterweight, silver arm, head shell) for the GPU, at the screen's pixel size.
    /// </summary>
    private void SendTurntable()
    {
        double k = VisualTreeHelper.GetDpi(this).DpiScaleX * Settings.Current.IslandScale;
        double d = 120 * 0.9 * k, r = d / 2;
        var record = new DrawingVisual();
        using (var g = record.RenderOpen())
        {
            var c = new Point(r, r);
            g.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x10)), null, c, r, r);
            for (double gr = r * 0.5; gr < r * 0.97; gr += 1.6 * k)
                g.DrawEllipse(null, new Pen(new SolidColorBrush(((int)(gr / k) % 3 == 0) ? Color.FromRgb(0x24, 0x24, 0x29) : Color.FromRgb(0x16, 0x16, 0x1A)), 0.7 * k), c, gr, gr);
            g.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x36)), 1.4 * k), c, r - 0.7 * k, r - 0.7 * k);
            // Light falling on the vinyl: two soft bands across it.
            var sheen = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.18));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x26, 255, 255, 255), 0.32));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.45));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.58));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x18, 255, 255, 255), 0.7));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.82));
            g.DrawEllipse(sheen, null, c, r, r);
            g.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x40)), 1 * k), c, r * 0.46 + 1.2 * k, r * 0.46 + 1.2 * k);
        }
        var disc = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(d), (int)Math.Ceiling(d), 96, 96, PixelFormats.Pbgra32);
        disc.Render(record);
        disc.Freeze();

        double w = 34 * k, h = 92 * k;
        var pivot = new Point(22 * k, 13 * k);
        var armVisual = new DrawingVisual();
        using (var g = armVisual.RenderOpen())
        {
            var silver = new LinearGradientBrush(Color.FromRgb(0xE4, 0xE5, 0xEA), Color.FromRgb(0x86, 0x88, 0x90), 0);
            g.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x52)), null, new Rect(pivot.X - 4 * k, 1 * k, 8 * k, 6 * k)); // counterweight
            var end = new Point(9 * k, 78 * k);
            g.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0x5A, 0x5C, 0x64)), 3.4 * k) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, pivot, end);
            g.DrawLine(new Pen(silver, 2.2 * k) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, pivot, end);
            g.PushTransform(new RotateTransform(14, end.X, end.Y));
            g.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x9A, 0x9C, 0xA4)), new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x40)), 0.8 * k), new Rect(end.X - 4 * k, end.Y - 2 * k, 8 * k, 13 * k), 1.5 * k, 1.5 * k);
            g.Pop();
            g.DrawEllipse(new RadialGradientBrush(Color.FromRgb(0xC8, 0xCA, 0xD0), Color.FromRgb(0x50, 0x52, 0x5A)), new Pen(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x30)), 1 * k), pivot, 8 * k, 8 * k);
            g.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x36)), null, pivot, 3.2 * k, 3.2 * k);
        }
        var arm = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(w), (int)Math.Ceiling(h), 96, 96, PixelFormats.Pbgra32);
        arm.Render(armVisual);
        arm.Freeze();
        _surface.SetTurntable(disc, arm, new System.Numerics.Vector2((float)pivot.X, (float)pivot.Y));
        _surface.SetSpinning(_media.IsPlaying);
    }

    /// <summary>After a change of resolution or screen: fit the island to the new size.</summary>
    public void ScreenChanged()
    {
        ApplyLook();
        RefreshActions();
    }
}
