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
    private const int GamePage = 7; // "Spiel" in the settings window

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
        PetTag.Text = $"+ {s.PetName.ToUpperInvariant()} / {PixelPet.Kinds[Math.Clamp(s.PetKind, 0, 2)].ToUpperInvariant()}";
        PetMood.Text = _media.IsPlaying ? "♪ TANZT" : "CHILLT";
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

    /// <summary>The character only moves while it can be seen: island open on the start page.</summary>
    private void ShowPet() => Pet.Running = _expanded && _page == 0;

    /// <summary>After a change of resolution or screen: fit the island to the new size.</summary>
    public void ScreenChanged()
    {
        ApplyLook();
        RefreshActions();
    }
}
