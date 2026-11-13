using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace DesktopOverlay;

/// <summary>The one place for all settings. Everything applies immediately; the window frees its content when closed.</summary>
public partial class SettingsWindow : Window
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    private static readonly string[] BackgroundColors = { "#0A0A0A", "#0B1220", "#16171A", "#26282C" };
    private static readonly string[] BackgroundNames = { "Schwarz", "Nachtblau", "Anthrazit", "Grau" };
    private static readonly string[] WidgetColors = { "#E6E6E6", "#8E8E8E", "#30D158", "#FFB340" };
    private static readonly string[] WidgetColorNames = { "Weiß", "Grau", "Grün", "Bernstein" };
    private static readonly Brush Selected = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)); // terminal look: the chosen item is inverted
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0A));
    private static readonly Brush Field = new SolidColorBrush(Color.FromArgb(0x10, 255, 255, 255));
    private static readonly Brush Card = new SolidColorBrush(Color.FromArgb(0x08, 255, 255, 255));

    internal static int _lastPage;

    private readonly Settings _s = Settings.Current;
    private App App => (App)Application.Current;
    private Brush Dim => (Brush)FindResource("Dim");
    private readonly (string Glyph, string Title, Action Build)[] _pages;

    public SettingsWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1, round = 2; // dark title area, rounded window corners
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        };
        _pages = new (string, string, Action)[]
        {
            ("", "Allgemein", General), ("", "Aussehen", Look), ("", "Taskleiste", Taskbar),
            ("", "Island", Island), ("", "Widgets", Widgets), ("", "Themes", Themes), ("", "Effekte", Effects),
            ("\uE7FC", "Spiel", Game),
        };
        for (int i = 0; i < _pages.Length; i++) Nav.Children.Add(NavItem(i));
        Show(_lastPage);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- navigation ----------

    private FrameworkElement NavItem(int index)
    {
        var (glyph, title, _) = _pages[index];
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = $"{index + 1:00} {title.ToUpperInvariant()}", FontFamily = Theme.Mono, FontSize = 11, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var item = new Border { CornerRadius = new CornerRadius(2), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand, Background = Brushes.Transparent, Child = row };
        Feedback.SetPress(item, true);
        item.MouseLeftButtonUp += (_, _) => Show(index);
        return item;
    }

    private void Show(int index)
    {
        _lastPage = index;
        for (int i = 0; i < Nav.Children.Count; i++)
        {
            var item = (Border)Nav.Children[i];
            item.Background = i == index ? Selected : Brushes.Transparent;
            TextElement.SetForeground(item, i == index ? Ink : Brushes.White);
        }
        Page.Children.Clear();
        Page.Children.Add(new TextBlock { Text = _pages[index].Title, FontSize = 26, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) });
        Page.Children.Add(new TextBlock { Text = $"ONYX / EINSTELLUNGEN / {index + 1:00}", FontFamily = Theme.Mono, FontSize = 10, Foreground = Dim, Margin = new Thickness(1, 0, 0, 10) });
        _pages[index].Build();
        PageScroll.ScrollToTop();
        // The new page settles in from slightly below instead of popping in.
        var rise = new TranslateTransform(0, Theme.ReduceMotion ? 0 : 8);
        Page.RenderTransform = rise;
        Page.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, Theme.Ms(140)));
        rise.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(0, Theme.Ms(240)) { EasingFunction = Theme.Settle });
    }

    private void Reload() => Show(_lastPage);

    /// <summary>Debug aid for --snapshot: renders every page to a PNG file.</summary>
    public void RenderPages(string dir)
    {
        var root = (FrameworkElement)Content;
        var dpi = VisualTreeHelper.GetDpi(root);
        for (int i = 0; i < _pages.Length; i++)
        {
            Show(i);
            Page.BeginAnimation(OpacityProperty, null);
            Page.Opacity = 1;
            Page.RenderTransform = null;
            UpdateLayout();
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded); // let sliders fill in their values
            var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            image.Render(root);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(dir, $"Settings{i}.png"));
            png.Save(file);
        }
    }

    // ---------- building blocks ----------

    private StackPanel Section(string title)
    {
        Page.Children.Add(new TextBlock { Text = "+ " + title.ToUpperInvariant(), FontFamily = Theme.Mono, FontSize = 10, Foreground = Dim, Margin = new Thickness(2, 16, 0, 6) });
        var body = new StackPanel();
        Page.Children.Add(new Border { Background = Card, BorderBrush = Theme.Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), Padding = new Thickness(14, 6, 14, 6), Child = body });
        return body;
    }

    private static Grid Line(string label, string hint = null)
    {
        var row = new Grid { MinHeight = 40 };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 12, 6) };
        text.Children.Add(new TextBlock { Text = label, FontSize = 13 });
        if (hint != null) text.Children.Add(new TextBlock { Text = hint, FontFamily = Theme.Mono, FontSize = 10, Opacity = 0.55, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        row.Children.Add(text);
        return row;
    }

    private void Switch(StackPanel into, string label, bool value, Action<bool> change, string hint = null)
    {
        var row = Line(label, hint);
        var toggle = new ToggleSwitch(value) { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        ((FrameworkElement)row.Children[0]).Margin = new Thickness(0, 6, 56, 6); // long hints wrap before the switch
        toggle.Toggled += v => { change(v); _s.Save(); };
        row.Children.Add(toggle);
        into.Children.Add(row);
    }

    private void Slider(StackPanel into, string label, double min, double max, Func<double> get, Action<double> set, Func<double, string> format)
    {
        var row = Line(label);
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        var slider = new VolumeSlider(() => (float)((get() - min) / (max - min)), v => { set(min + v * (max - min)); _s.Save(); }) { VerticalAlignment = VerticalAlignment.Center };
        var value = new TextBlock { FontFamily = Theme.Mono, FontSize = 11, Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        slider.Changed += (_, _) => value.Text = format(get());
        slider.Loaded += (_, _) => slider.Refresh();
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(value, 2);
        row.Children.Add(slider);
        row.Children.Add(value);
        into.Children.Add(row);
    }

    /// <summary>A label with a row of options, one of which is selected.</summary>
    private void Pick(StackPanel into, string label, string[] options, int selected, Action<int> apply)
    {
        var row = Line(label);
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var chips = new UniformGrid { Rows = 1, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < options.Length; i++)
        {
            int index = i;
            var chip = new Border
            {
                CornerRadius = new CornerRadius(2), Padding = new Thickness(6, 5, 6, 5), Margin = new Thickness(2, 4, 2, 4), Cursor = Cursors.Hand,
                BorderBrush = Theme.Res("Line"), BorderThickness = new Thickness(1),
                Background = i == selected ? Selected : Brushes.Transparent,
                Child = new TextBlock { Text = options[i].ToUpperInvariant(), FontFamily = Theme.Mono, FontSize = 10, Foreground = i == selected ? Ink : Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
            };
            chip.MouseLeftButtonUp += (_, _) =>
            {
                foreach (Border c in chips.Children) { c.Background = c == chip ? Selected : Brushes.Transparent; ((TextBlock)c.Child).Foreground = c == chip ? Ink : Brushes.White; }
                apply(index);
                _s.Save();
            };
            chips.Children.Add(chip);
        }
        Grid.SetColumn(chips, 1);
        row.Children.Add(chips);
        into.Children.Add(row);
    }

    private Button Action(string text, Action click, bool accent = false)
    {
        var button = new Button
        {
            Style = (Style)FindResource("Key"), Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Background = accent ? Theme.AccentBrush : Brushes.Transparent,
            Foreground = accent ? Ink : Brushes.White,
            Content = new TextBlock { Text = text.ToUpperInvariant(), FontFamily = Theme.Mono, FontSize = 10.5 },
        };
        button.Click += (_, _) => click();
        return button;
    }

    private void ActionRow(StackPanel into, string label, string hint, string button, Action click, bool accent = false)
    {
        var row = Line(label, hint);
        row.Children.Add(Action(button, click, accent));
        into.Children.Add(row);
    }

    // ---------- pages ----------

    private void General()
    {
        var start = Section("Start und Anzeige");
        Switch(start, "Mit Windows starten", Settings.Autostart, v => Settings.Autostart = v);
        Switch(start, "Island immer ausgeklappt", _s.AlwaysExpanded, v => { _s.AlwaysExpanded = v; App.Island.ApplyAlwaysExpanded(); });
        Switch(start, "Desktop-Widgets anzeigen", _s.ShowWidgets, v => { _s.ShowWidgets = v; App.ApplyWidgetVisibility(); });
        Switch(start, "Speicher sparen", _s.SaveMemory, v => _s.SaveMemory = v, "Spart rund 45 MB, Animationen können weniger flüssig sein. Gilt nach einem Neustart von Onyx.");

        var system = Section("System");
        if (UpdateService.Available is { } version) ActionRow(system, $"Version {version} ist verfügbar", null, "Aktualisieren", App.InstallUpdate, accent: true);
        if (SystemMonitor.CpuTempProblem is { } problem)
            ActionRow(system, "CPU-Temperatur", problem, SystemMonitor.IsElevated ? "Treiber installieren" : "Als Admin starten", App.FixCpuTemp);
        ActionRow(system, "Onyx beenden", "Die Windows-Taskleiste kommt zurück.", "Beenden", App.Quit);
    }

    private void Look()
    {
        var themes = Section("Theme");
        Pick(themes, "Vorlage", Theme.Presets.Select(p => p.Name).ToArray(), Array.FindIndex(Theme.Presets, p => p.Accent == _s.AccentColor && p.Island == _s.IslandColor), i => { Theme.UsePreset(i); Reload(); });

        var style = Section("Stil");
        Pick(style, "Akzentfarbe", new[] { "Weiß", "Grün", "Rot", "Bernstein" }, Array.IndexOf(Theme.AccentColors, _s.AccentColor), i => { _s.AccentColor = Theme.AccentColors[i]; Theme.ApplyAll(); Reload(); });
        Pick(style, "Schrift", Theme.Fonts.Select(f => f.Label).ToArray(), Array.FindIndex(Theme.Fonts, f => f.Family == _s.FontFamily), i => { _s.FontFamily = Theme.Fonts[i].Family; Theme.ApplyFont(); });
        Pick(style, "Rand", new[] { "Keiner", "Dezent", "Akzent", "Lichtlauf" }, _s.RimStyle, i => { _s.RimStyle = i; Theme.ApplyAll(); });
        Pick(style, "Oberfläche", new[] { "Glatt", "Punktraster" }, _s.Grain ? 1 : 0, i => { _s.Grain = i == 1; Theme.ApplyAll(); });
        Pick(style, "Animationen", new[] { "Schnell", "Normal", "Aus" }, _s.AnimationSpeed, i => _s.AnimationSpeed = i);
    }

    private void Taskbar()
    {
        var bar = App.Taskbar;
        var look = Section("Aussehen");
        Pick(look, "Farbe", BackgroundNames, Array.IndexOf(BackgroundColors, _s.TaskbarColor), i => { _s.TaskbarColor = BackgroundColors[i]; bar.ApplyLook(); });
        Slider(look, "Transparenz", 0, 1, () => _s.Transparency, v => { _s.TaskbarTransparency = Math.Round(v, 2); bar.ApplyLook(); }, v => $"{v * 100:0} %");

        var behavior = Section("Verhalten");
        Switch(behavior, "Automatisch einklappen", _s.TaskbarAutoHide, v => { _s.TaskbarAutoHide = v; bar.ApplyAutoHide(); }, "Erscheint, wenn die Maus den unteren Rand berührt.");

        var icons = Section("Inhalt");
        Pick(icons, "App-Symbole", new[] { "Farbig", "Weiß", "Grau", "Akzent" }, _s.IconTint, i => { _s.IconTint = i; bar.ApplyLook(); });
        Pick(icons, "App-Anzeige", new[] { "Strich", "Punkt", "Leuchten", "Aus" }, _s.IndicatorStyle, i => { _s.IndicatorStyle = i; bar.ApplyLook(); });

        var size = Section("Größe");
        Slider(size, "Höhe", 38, 64, () => _s.TaskbarHeight, v => { _s.TaskbarHeight = Math.Round(v); bar.ApplyLook(); }, v => $"{v:0} px");
        Slider(size, "Symbolgröße", 18, 32, () => _s.TaskbarIconSize, v => { _s.TaskbarIconSize = Math.Round(v); bar.ApplyLook(); }, v => $"{v:0} px");
        Slider(size, "Ecken", 0, 25, () => _s.TaskbarRadius, v => { _s.TaskbarRadius = Math.Round(v); bar.ApplyLook(); }, v => $"{v:0} px");
    }

    private void Island()
    {
        var island = App.Island;
        var look = Section("Aussehen");
        Pick(look, "Farbe", BackgroundNames, Array.IndexOf(BackgroundColors, _s.IslandColor), i => { _s.IslandColor = BackgroundColors[i]; island.ApplyLook(); });
        Pick(look, "Form", new[] { "Schwebend", "Angedockt", "Kapsel" }, _s.IslandShape, i => { _s.IslandShape = i; island.ApplyLook(); });
        Pick(look, "Bewegung", new[] { "Weich", "Federnd", "Direkt" }, _s.MotionStyle, i => _s.MotionStyle = i);

        var size = Section("Größe");
        Slider(size, "Größe", 0.8, 1.2, () => _s.IslandScale, v => { _s.IslandScale = Math.Round(v, 2); island.ApplyLook(); }, v => $"{v * 100:0} %");
        Slider(size, "Ecken", 0.2, 1, () => _s.IslandCorners, v => { _s.IslandCorners = Math.Round(v, 2); island.ApplyLook(); }, v => $"{v * 100:0} %");
        Slider(size, "Deckkraft", 0.4, 1, () => _s.IslandOpacity, v => { _s.IslandOpacity = Math.Round(v, 2); island.ApplyLook(); }, v => $"{v * 100:0} %");
    }

    private void Widgets()
    {
        var look = Section("Aussehen");
        Pick(look, "Farbe", WidgetColorNames, Array.IndexOf(WidgetColors, _s.WidgetColor), i => { _s.WidgetColor = WidgetColors[i]; App.ApplyWidgetLook(); });
        Pick(look, "CPU und RAM", new[] { "Linie", "Balken", "Ringe" }, _s.WidgetGraph, i => _s.WidgetGraph = i);


        var cards = Section("Karten");
        foreach (var (key, label, hint) in new[]
        {
            ("Stats", "System", "Netz, GPU, CPU, RAM und Laufwerke."), ("Temp", "CPU-Temperatur", null),
            ("Music", "Musik", "Cover, Tasten und Pegel."), ("Terminal", "Terminal", "fetch, cava, life, orb und rain."), ("Bonsai", "Bonsai", "Wächst, will gegossen werden."),
            ("Weather", "Wetter", "Von Open-Meteo, braucht einen Ort."), ("Clock", "Uhr", null), ("Calendar", "Kalender", null),
            ("Signal", "Signal", "Augen, Puls oder Uhr in Punkten."), ("Koi", "Koi-Teich", "Klick ins Wasser macht Wellen."),
        })
            Switch(cards, label, _s.CardOn(key), v =>
            {
                _s.Cards[key] = v;
                if (v && key == "Stats") foreach (var part in _s.Cards.Keys.Where(k => k.StartsWith("Stats-")).ToList()) _s.Cards.Remove(part); // also its single parts
                App.ApplyWidgetVisibility();
            }, hint);

        var weather = Section("Wetter");
        var place = Line("Ort", "Nur dieser Name geht an Open-Meteo, um das Wetter zu finden.");
        place.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        place.ColumnDefinitions.Add(new ColumnDefinition());
        place.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var city = new TextBox
        {
            Text = _s.WeatherCity, FontFamily = Theme.Mono, FontSize = 12, Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(8, 6, 8, 6),
            Background = Brushes.Transparent, VerticalContentAlignment = VerticalAlignment.Center,
        };
        var cityField = new Border { Background = Field, BorderBrush = Theme.Res("LineHi"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Child = city };
        var use = Action("Übernehmen", () =>
        {
            _s.WeatherCity = city.Text.Trim();
            _s.WeatherPlace = null;
            _s.Save();
            App.Widget<WeatherCard>()?.Refresh();
        });
        Grid.SetColumn(cityField, 1);
        Grid.SetColumn(use, 2);
        place.Children.Add(cityField);
        place.Children.Add(use);
        weather.Children.Add(place);
        var layout = Section("Anordnung");
        Pick(layout, "Aufteilung", new[] { "Ein Block", "Einzeln" }, _s.WidgetsFree ? 1 : 0, i => { _s.WidgetsFree = i == 1; App.ApplyWidgetLayout(); });
        ActionRow(layout, "Widgets verschieben", "Widgets mit der Maus an ihren Platz ziehen.", WidgetMover.Editing ? "Fertig" : "Anordnen", () =>
        {
            if (!_s.ShowWidgets) { _s.ShowWidgets = true; _s.Save(); App.ApplyWidgetVisibility(); }
            WidgetMover.SetEditing(!WidgetMover.Editing);
            Reload();
        }, accent: WidgetMover.Editing);

        var size = Section("Größe");
        Slider(size, "Größe", 0.7, 1.3, () => _s.WidgetScale, v => { _s.WidgetScale = Math.Round(v, 2); App.ApplyWidgetLook(); }, v => $"{v * 100:0} %");
        Slider(size, "Deckkraft", 0.3, 1, () => _s.WidgetOpacity, v => { _s.WidgetOpacity = Math.Round(v, 2); App.ApplyWidgetLook(); }, v => $"{v * 100:0} %");
    }

    private void Themes()
    {
        var names = Theme.Names().ToArray();
        var time = Section("Tageszeit");
        Switch(time, "Automatisch wechseln", _s.AutoTheme, v => { _s.AutoTheme = v; Theme.CheckTimeOfDay(force: true); }, "Um 7 und um 19 Uhr.");
        Pick(time, "Am Tag", names, Array.IndexOf(names, _s.DayTheme), i => { _s.DayTheme = names[i]; Theme.CheckTimeOfDay(force: true); });
        Pick(time, "In der Nacht", names, Array.IndexOf(names, _s.NightTheme), i => { _s.NightTheme = names[i]; Theme.CheckTimeOfDay(force: true); });

        const string filter = "Onyx-Theme (*.onyxtheme)|*.onyxtheme";
        var own = Section("Eigene Themes");
        var save = Line("Aktuelles Design speichern");
        save.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        save.ColumnDefinitions.Add(new ColumnDefinition());
        save.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBox
        {
            Text = "Mein Theme", FontFamily = Theme.Mono, FontSize = 12, Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(8, 6, 8, 6),
            Background = Brushes.Transparent, VerticalContentAlignment = VerticalAlignment.Center,
        };
        var field = new Border { Background = Field, BorderBrush = Theme.Res("LineHi"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Child = name };
        var store = Action("Speichern", () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) return;
            Theme.SaveCurrent(name.Text.Trim());
            Reload();
        });
        Grid.SetColumn(field, 1);
        Grid.SetColumn(store, 2);
        save.Children.Add(field);
        save.Children.Add(store);
        own.Children.Add(save);

        foreach (var theme in _s.CustomThemes.Keys.ToList())
        {
            var row = Line(theme);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(Action("Anwenden", () => { Theme.Use(theme); Reload(); }));
            var export = Action("Teilen", () =>
            {
                var dialog = new Microsoft.Win32.SaveFileDialog { FileName = theme + ".onyxtheme", Filter = filter };
                if (dialog.ShowDialog(this) == true) Theme.Export(theme, dialog.FileName);
            });
            export.Margin = new Thickness(6, 0, 6, 0);
            buttons.Children.Add(export);
            buttons.Children.Add(Action("Löschen", () => { _s.CustomThemes.Remove(theme); _s.Save(); Reload(); }));
            row.Children.Add(buttons);
            own.Children.Add(row);
        }
        ActionRow(own, "Theme-Datei importieren", null, "Importieren", () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = filter };
            if (dialog.ShowDialog(this) == true && Theme.Import(dialog.FileName) is { } imported) { Theme.Use(imported); Reload(); }
        });
    }


    private void Game()
    {
        var cross = App.Crosshair;
        var c = Section("Crosshair");
        Switch(c, "Crosshair anzeigen", _s.CrosshairOn, v => { _s.CrosshairOn = v; cross.Apply(); }, "Über Spielen im Rahmenlos- oder Fenstermodus; exklusives Vollbild zeichnet über alles.");
        Pick(c, "Form", CrosshairWindow.Styles, _s.CrosshairStyle, i => { _s.CrosshairStyle = i; cross.Apply(); });
        Pick(c, "Farbe", CrosshairWindow.ColorNames, Array.IndexOf(CrosshairWindow.Colors, _s.CrosshairColor), i => { _s.CrosshairColor = CrosshairWindow.Colors[i]; cross.Apply(); });
        Slider(c, "Länge", 2, 30, () => _s.CrosshairSize, v => { _s.CrosshairSize = Math.Round(v); cross.Apply(); }, v => $"{v:0} px");
        Slider(c, "Dicke", 1, 6, () => _s.CrosshairThickness, v => { _s.CrosshairThickness = Math.Round(v); cross.Apply(); }, v => $"{v:0} px");
        Slider(c, "Abstand", 0, 15, () => _s.CrosshairGap, v => { _s.CrosshairGap = Math.Round(v); cross.Apply(); }, v => $"{v:0} px");
        Slider(c, "Deckkraft", 0.2, 1, () => _s.CrosshairOpacity, v => { _s.CrosshairOpacity = Math.Round(v, 2); cross.Apply(); }, v => $"{v * 100:0} %");
        Switch(c, "Schwarzer Umriss", _s.CrosshairOutline, v => { _s.CrosshairOutline = v; cross.Apply(); }, "Besser sichtbar auf hellem Hintergrund.");

        var d = Section("Anzeige");
        if (Vibrance.Available)
        {
            Slider(d, "Farbsättigung", 0, 1, () => Vibrance.Read() ?? 0.5, v => { Vibrance.Set(v); _s.VibranceLevel = Math.Round(v, 3); }, v => $"{v * 100:0} %");
            ActionRow(d, "Sättigung zurücksetzen", "Zurück zum Wert des NVIDIA-Treibers.", "Standard", () => { Vibrance.Set(Vibrance.Default()); _s.VibranceLevel = null; _s.Save(); Reload(); });
        }
        else d.Children.Add(new TextBlock { Text = "Farbsättigung braucht eine NVIDIA-Grafikkarte.", FontFamily = Theme.Mono, FontSize = 10, Foreground = Dim, Margin = new Thickness(0, 10, 0, 10) });
        var mode = Display.Current();
        d.Children.Add(new TextBlock { Text = $"AKTUELL {mode}  ·  WECHSELN ÜBER DIE ISLAND", FontFamily = Theme.Mono, FontSize = 10, Foreground = Dim, Margin = new Thickness(0, 6, 0, 10) });

        var p = Section("Charakter");
        Pick(p, "Art", PixelPet.Kinds, _s.PetKind, i => _s.PetKind = i);
        Pick(p, "Farbe", PixelPet.ColorNames, Array.IndexOf(PixelPet.Colors, _s.PetColor), i => _s.PetColor = PixelPet.Colors[i]);
        Pick(p, "Hut", PixelPet.Hats, _s.PetHat, i => _s.PetHat = i);
        var row = Line("Name");
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBox
        {
            Text = _s.PetName, MaxLength = 12, FontFamily = Theme.Mono, FontSize = 12, Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 6, 8, 6), Background = Brushes.Transparent, VerticalContentAlignment = VerticalAlignment.Center,
        };
        var field = new Border { Background = Field, BorderBrush = Theme.Res("LineHi"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Child = name };
        var use = Action("Übernehmen", () => { _s.PetName = string.IsNullOrWhiteSpace(name.Text) ? "Pixel" : name.Text.Trim(); _s.Save(); });
        Grid.SetColumn(field, 1);
        Grid.SetColumn(use, 2);
        row.Children.Add(field);
        row.Children.Add(use);
        p.Children.Add(row);
    }
    private void Effects()
    {
        var effects = Section("Island");
        Pick(effects, "Musik-Glow", new[] { "Aus", "Akzent", "Cover" }, _s.MusicGlow, i => { _s.MusicGlow = i; App.Island.ApplyGlow(); });
        Switch(effects, "Hitze-Alarm", _s.HeatAlarm, v => _s.HeatAlarm = v, "Die Island wird rot, wenn CPU oder GPU zu heiß werden.");
        Slider(effects, "Warnen ab", 70, 100, () => _s.HeatLimit, v => _s.HeatLimit = Math.Round(v), v => $"{v:0} °C");

        var profiles = Section("Profile");
        profiles.Children.Add(new TextBlock { Text = "Solange eine dieser Apps im Vordergrund ist, gilt ihr Theme.", FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 6) });
        foreach (var (program, theme) in _s.Profiles.ToList())
        {
            var row = Line(Path.GetFileNameWithoutExtension(program));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var next = Action(theme, () =>
            {
                var names = Theme.Names();
                _s.Profiles[program] = names[(names.IndexOf(_s.Profiles[program]) + 1) % names.Count];
                _s.Save();
                Reload();
            });
            next.ToolTip = "Klicken wechselt das Theme";
            next.Margin = new Thickness(0, 0, 6, 0);
            buttons.Children.Add(next);
            buttons.Children.Add(Action("Entfernen", () => { _s.Profiles.Remove(program); _s.Save(); Reload(); }));
            row.Children.Add(buttons);
            profiles.Children.Add(row);
        }
        Button add = null;
        add = Action("Hinzufügen", async () =>
        {
            var apps = (await Task.Run(FlyoutWindow.CollectRunningApps)).Where(a => a.HasWindow).OrderBy(a => a.Name).ToList();
            var menu = new ContextMenu { PlacementTarget = add, Placement = PlacementMode.Bottom };
            foreach (var running in apps)
            {
                var item = new MenuItem { Header = running.Name };
                item.Click += (_, _) => { _s.Profiles[Path.GetFileName(running.Path)] = Theme.Names()[0]; _s.Save(); Reload(); };
                menu.Items.Add(item);
            }
            menu.IsOpen = menu.Items.Count > 0;
        });
        var addRow = Line("Laufende App");
        addRow.Children.Add(add);
        profiles.Children.Add(addRow);
    }
}
