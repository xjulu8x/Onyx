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
            ("", "Allgemein", General), ("", "Aussehen", Look), ("", "Taskleiste", Taskbar),
            ("", "Island", Island), ("", "Widgets", Widgets), ("", "Effekte", Effects),
            ("", "Spiel", Game), ("", "Charakter", Character),
        };
        BuildNav();
        Show(_lastPage);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- navigation: logo, a switch box, main pages, a fold-out group for the widgets ----------

    private readonly List<(Border Item, int Page, int Tab)> _nav = new();
    private static bool _widgetsOpen = true;

    private void BuildNav()
    {
        Nav.Children.Clear();
        _nav.Clear();

        // Switch box like a workspace selector: version, and a menu with update and quit.
        var box = new Border { Background = new SolidColorBrush(Color.FromArgb(0x10, 255, 255, 255)), BorderBrush = Theme.Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 10, 10, 10), Margin = new Thickness(0, 0, 0, 14), Cursor = Cursors.Hand };
        var boxRow = new Grid();
        boxRow.Children.Add(new TextBlock { Text = UpdateService.Available is { } v ? $"Update {v} bereit" : $"Version {typeof(App).Assembly.GetName().Version?.ToString(3)}", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        boxRow.Children.Add(new TextBlock { Text = "\n", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 7, LineHeight = 8, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Dim });
        box.Child = boxRow;
        Feedback.SetPress(box, true);
        box.MouseLeftButtonUp += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = box, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            void Entry(string glyph, string text, Action act) { var item = new MenuItem { Header = text, Icon = glyph }; item.Click += (_, _) => act(); menu.Items.Add(item); }
            if (UpdateService.Available != null) Entry("", "Jetzt aktualisieren", App.InstallUpdate);
            Entry("", "Widgets anordnen", () => WidgetMover.SetEditing(!WidgetMover.Editing));
            Entry("", "Onyx beenden", App.Quit);
            menu.IsOpen = true;
        };
        Nav.Children.Add(box);

        Border Item(string glyph, string text, int page, int tab = -1, Brush dot = null, string badge = null, bool child = false)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(child ? 18 : 28) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (dot != null) row.Children.Add(new System.Windows.Shapes.Rectangle { Width = 7, Height = 7, Fill = dot, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center });
            else row.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            var label = new TextBlock { Text = text, FontSize = child ? 12.5 : 13.5, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            if (badge != null)
            {
                var b = new TextBlock { Text = badge, FontFamily = Theme.Mono, FontSize = 10, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(b, 2);
                row.Children.Add(b);
            }
            var item = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(child ? 18 : 0, 1, 0, 1), Cursor = Cursors.Hand, Background = Brushes.Transparent, Child = row };
            Feedback.SetPress(item, true);
            item.MouseLeftButtonUp += (_, _) => { if (tab >= 0) _widgetTab = tab; Show(page); };
            _nav.Add((item, page, tab));
            Nav.Children.Add(item);
            return item;
        }
        Border Divider() { var d = new Border { Height = 1, Background = Theme.Res("Line"), Margin = new Thickness(0, 12, 0, 12) }; Nav.Children.Add(d); return d; }

        for (int i = 0; i < 4; i++) Item(_pages[i].Glyph, _pages[i].Title, i);
        Divider();

        // Widgets: a group that folds open, its categories marked with colored dots; the badge counts the active widgets.
        int active = new[] { "Stats", "Temp", "Music", "Terminal", "Bonsai", "Weather", "Clock", "Calendar", "Signal", "Koi", "Steam", "Discord" }.Count(_s.CardOn);
        var head = new Grid { Cursor = Cursors.Hand, Background = Brushes.Transparent, Margin = new Thickness(0, 1, 0, 1) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock { Text = _pages[4].Glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        var headText = new TextBlock { Text = $"Widgets  ·  {active}", FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(headText, 1);
        head.Children.Add(headText);
        var chevron = new TextBlock { Text = _widgetsOpen ? "" : "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(chevron, 2);
        head.Children.Add(chevron);
        var headBox = new Border { Padding = new Thickness(10, 8, 10, 8), Child = head };
        headBox.MouseLeftButtonUp += (_, _) => { _widgetsOpen = !_widgetsOpen; BuildNav(); Highlight(); };
        Nav.Children.Add(headBox);
        if (_widgetsOpen)
        {
            var colors = new[] { "#E6E6E6", "#4FA8FF", "#C58BFF", "#FF453A", "#30D158" };
            var names = new[] { "Allgemein", "System", "Zeit & Wetter", "Musik & Spaß", "Freunde" };
            for (int i = 0; i < names.Length; i++)
                Item(null, names[i], 4, i, new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i])), child: true);
        }
        Divider();
        for (int i = 5; i < _pages.Length; i++) Item(_pages[i].Glyph, _pages[i].Title, i);
    }

    private void Highlight()
    {
        foreach (var (item, page, tab) in _nav)
        {
            bool on = page == _lastPage && (tab < 0 || tab == _widgetTab);
            item.Background = on ? new SolidColorBrush(Color.FromArgb(0x16, 255, 255, 255)) : Brushes.Transparent;
            item.BorderBrush = on ? Theme.Res("LineHi") : null;
            item.BorderThickness = new Thickness(on ? 1 : 0);
            TextElement.SetForeground(item, on || page != 4 ? Brushes.White : Dim);
        }
    }

    private void Show(int index)
    {
        _lastPage = index;
        Highlight();
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
        ((FrameworkElement)row.Children[0]).Margin = new Thickness(0, 6, 150, 6); // long hints wrap before the key
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
        // Just colors: click a field, it applies at once everywhere.
        string[] accents = { "#E6E6E6", "#30D158", "#4FD1C5", "#4FA8FF", "#7FA6F0", "#C58BFF", "#FF6B8A", "#FF453A", "#FF7A45", "#FFB340" };
        string[] surfaces = { "#0A0A0A", "#121214", "#16171A", "#1E1F23", "#26282C", "#0B1220", "#101A14", "#1A1210", "#16101C" };
        string[] texts = { "#E6E6E6", "#FFFFFF", "#8E8E8E", "#30D158", "#4FD1C5", "#FFB340", "#FF6B8A", "#C58BFF" };
        var colors = Section("Farben");
        Swatches(colors, "Akzent", _s.AccentColor, c => { _s.AccentColor = c; Theme.ApplyAll(); }, accents);
        Swatches(colors, "Island", _s.IslandColor, c => { _s.IslandColor = c; App.Island.ApplyLook(); }, surfaces);
        Swatches(colors, "Dock", _s.TaskbarColor, c => { _s.TaskbarColor = c; App.Taskbar.ApplyLook(); }, surfaces);
        Swatches(colors, "Widget-Schrift", _s.WidgetColor, c => { _s.WidgetColor = c; App.ApplyWidgetLook(); }, texts);
        var surface = Section("Fläche");
        Switch(surface, "Punktraster", _s.Grain, v => { _s.Grain = v; Theme.ApplyAll(); }, "Feine Punkte auf Island, Dock und Widgets.");
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

    private static int _widgetTab;

    private void Widgets()
    {
        // The categories are chosen in the side bar (the colored dots under "Widgets").

        void Cards(string title, params (string Key, string Label, string Hint)[] list)
        {
            var section = Section(title);
            foreach (var (key, label, hint) in list)
                Switch(section, label, _s.CardOn(key), v =>
                {
                    _s.Cards[key] = v;
                    if (v && key == "Stats") foreach (var part in _s.Cards.Keys.Where(k => k.StartsWith("Stats-")).ToList()) _s.Cards.Remove(part); // also its single parts
                    App.ApplyWidgetVisibility();
                }, hint);
        }

        switch (_widgetTab)
        {
            case 0:
                var look = Section("Aussehen");
                Pick(look, "Farbe", WidgetColorNames, Array.IndexOf(WidgetColors, _s.WidgetColor), i => { _s.WidgetColor = WidgetColors[i]; App.ApplyWidgetLook(); });
                Slider(look, "Größe", 0.7, 1.3, () => _s.WidgetScale, v => { _s.WidgetScale = Math.Round(v, 2); App.ApplyWidgetLook(); }, v => $"{v * 100:0} %");
                Slider(look, "Deckkraft", 0.3, 1, () => _s.WidgetOpacity, v => { _s.WidgetOpacity = Math.Round(v, 2); App.ApplyWidgetLook(); }, v => $"{v * 100:0} %");
                var layout = Section("Anordnung");
                ActionRow(layout, "Widgets verschieben", "Ziehen, Größe am Griff unten rechts ändern, mit × entfernen. Auch über den Hubbel an der Island.", WidgetMover.Editing ? "Fertig" : "Anordnen", () =>
                {
                    if (!_s.ShowWidgets) { _s.ShowWidgets = true; _s.Save(); App.ApplyWidgetVisibility(); }
                    WidgetMover.SetEditing(!WidgetMover.Editing);
                    Reload();
                }, accent: WidgetMover.Editing);
                ActionRow(layout, "Eigene Größen zurücksetzen", "Alle Widgets wieder in normaler Größe.", "Zurücksetzen", () => { _s.WidgetScales.Clear(); _s.Save(); App.ApplyWidgetLook(); });
                break;

            case 1:
                Cards("System", ("Stats", "System", "Netz, GPU, CPU, RAM und Laufwerke."), ("Temp", "CPU-Temperatur", null), ("Signal", "Signal", "Augen, Puls oder Uhr in Punkten, dazu Live-Werte."));
                var sys = Section("Darstellung");
                Pick(sys, "CPU und RAM", new[] { "Linie", "Balken", "Ringe" }, _s.WidgetGraph, i => _s.WidgetGraph = i);
                Pick(sys, "Aufteilung", new[] { "Ein Block", "Einzeln" }, _s.WidgetsFree ? 1 : 0, i => { _s.WidgetsFree = i == 1; App.ApplyWidgetLayout(); });
                break;

            case 2:
                Cards("Zeit", ("Clock", "Uhr", "Analog mit roter Sekunde."), ("Calendar", "Kalender", "Monat zum Blättern."), ("Weather", "Wetter", "Von Open-Meteo, braucht einen Ort."));
                var weather = Section("Wetter");
                TextSetting(weather, "Ort", "Nur dieser Name geht an Open-Meteo, um das Wetter zu finden.", _s.WeatherCity, false, text =>
                {
                    _s.WeatherCity = text;
                    _s.WeatherPlace = null;
                    App.Widget<WeatherCard>()?.Refresh();
                });
                break;

            case 3:
                Cards("Musik & Spaß", ("Music", "Musik", "Cover, Tasten und Pegel."), ("Terminal", "Terminal", "fetch, cava, life, orb und rain."),
                    ("Bonsai", "Bonsai", "Wächst, will gegossen werden."), ("Koi", "Koi-Teich", "Klick ins Wasser macht Wellen."));
                break;

            case 4:
                Cards("Freunde", ("Steam", "Steam", "Freunde online und ihre Spiele."), ("Discord", "Discord", "Server: online, Spiele und Sprachkanäle."));

                // Steam: nothing to type. The account comes from the Steam app; only the friends list has to be public.
                var steam = Section("Steam");
                string account = SteamCard.LocalAccount();
                steam.Children.Add(new TextBlock
                {
                    Text = account != null ? $"✓ STEAM-KONTO ERKANNT ({account})" : "STEAM NICHT GEFUNDEN – STEAM STARTEN UND ANMELDEN",
                    FontFamily = Theme.Mono, FontSize = 10, Foreground = account != null ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)) : Dim,
                    Margin = new Thickness(0, 10, 0, 4),
                });
                ActionRow(steam, "Freundesliste öffentlich machen", "Nur nötig, wenn das Widget „privat“ meldet.", "Öffnen", () => Ascii.Open("https://steamcommunity.com/my/edit/settings"));
                TextSetting(steam, "Anderes Profil", "Optional: Profil-Link, falls ein anderes Konto gezeigt werden soll.", _s.SteamId, false, text => { _s.SteamId = text; App.Widget<SteamCard>()?.Refresh(); });

                // Discord: three clicks. Discord only shows servers through a bot, so we guide through making one.
                var discord = Section("Discord");
                string appId = DiscordCard.AppId(_s.DiscordToken);
                Step(discord, "1", "Bot anlegen", "Im Developer-Portal „New Application“, dann links „Bot“ → „Reset Token“ → Kopieren.", "Portal öffnen",
                    () => Ascii.Open("https://discord.com/developers/applications?new_application=true"), appId != null);
                Step(discord, "2", "Token einfügen", appId != null ? "Token ist gespeichert." : "Kopierten Token aus der Zwischenablage übernehmen.", "Einfügen", () =>
                {
                    string token = Clipboard.ContainsText() ? Clipboard.GetText().Trim().Trim('"') : "";
                    if (DiscordCard.AppId(token) == null) { MessageBox.Show(this, "In der Zwischenablage ist kein Bot-Token.", "Onyx"); return; }
                    _s.DiscordToken = token;
                    _s.Save();
                    App.Widget<DiscordCard>()?.Refresh();
                    Reload();
                }, appId != null);
                Step(discord, "3", "Rechte einschalten", "Auf der Bot-Seite „Presence Intent“ und „Server Members Intent“ an, speichern.", "Bot-Seite",
                    () => Ascii.Open(appId != null ? $"https://discord.com/developers/applications/{appId}/bot" : "https://discord.com/developers/applications"), false);
                Step(discord, "4", "Auf Server einladen", "Wähle deinen Server; der Bot sieht nur diesen.", "Einladen",
                    () => { if (appId != null) Ascii.Open($"https://discord.com/oauth2/authorize?client_id={appId}&scope=bot&permissions=0"); }, false);
                if (appId != null) ActionRow(discord, "Bot trennen", "Löscht den Token aus Onyx.", "Trennen", () => { _s.DiscordToken = ""; _s.Save(); App.Widget<DiscordCard>()?.Refresh(); Reload(); });
                break;
        }
    }

    /// <summary>One numbered step of a setup: number (checked when done), title, hint and a key.</summary>
    private void Step(StackPanel into, string number, string title, string hint, string button, Action click, bool done)
    {
        var row = Line($"{(done ? "✓" : number)}  {title}", hint);
        ((FrameworkElement)row.Children[0]).Margin = new Thickness(0, 6, 150, 6);
        if (done) TextElement.SetForeground(row.Children[0], new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)));
        row.Children.Add(Action(button, click));
        into.Children.Add(row);
    }

    /// <summary>A text field with an "Übernehmen" key; secrets are shown as dots.</summary>
    private void TextSetting(StackPanel into, string label, string hint, string value, bool secret, Action<string> apply)
    {
        var row = Line(label, hint);
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Control box;
        Func<string> read;
        if (secret)
        {
            var pass = new PasswordBox { Password = value ?? "", FontFamily = Theme.Mono, FontSize = 12, Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(8, 6, 8, 6), Background = Brushes.Transparent, VerticalContentAlignment = VerticalAlignment.Center };
            box = pass;
            read = () => pass.Password;
        }
        else
        {
            var text = new TextBox { Text = value ?? "", FontFamily = Theme.Mono, FontSize = 12, Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(8, 6, 8, 6), Background = Brushes.Transparent, VerticalContentAlignment = VerticalAlignment.Center };
            box = text;
            read = () => text.Text;
        }
        var field = new Border { Background = Field, BorderBrush = Theme.Res("LineHi"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Child = box };
        var use = Action("Übernehmen", () => { apply(read().Trim()); _s.Save(); });
        Grid.SetColumn(field, 1);
        Grid.SetColumn(use, 2);
        row.Children.Add(field);
        row.Children.Add(use);
        into.Children.Add(row);
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

    }

    // ---------- character ----------

    private void Character()
    {
        // ---- stage: big live preview on the left, name, templates and quick keys on the right ----
        var preview = new PixelPet { Zoom = 2.2, Margin = new Thickness(0, 4, 0, 0) };
        var stage = new Border
        {
            Background = Theme.DotGrid(Color.FromRgb(0x0C, 0x0C, 0x0E), 0x20), BorderBrush = Theme.Res("Line"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2), Padding = new Thickness(14, 8, 14, 6), Child = preview,
        };
        var tag = new TextBlock { FontFamily = Theme.Mono, FontSize = 10, Foreground = Dim, Margin = new Thickness(2, 0, 0, 6) };
        var tiles = new List<Action>(); // every picture tile redraws itself when the character changes
        void Changed()
        {
            tag.Text = $"+ {_s.PetName.ToUpperInvariant()} · LIVE-VORSCHAU · KLICKEN ZUM ANTIPPEN";
            preview.InvalidateVisual();
            App.Island?.PetChanged();
            foreach (var refresh in tiles) refresh();
        }
        preview.Loaded += (_, _) => preview.Running = true;

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        top.Children.Add(stage);

        var side = new StackPanel();
        Grid.SetColumn(side, 2);
        top.Children.Add(side);
        side.Children.Add(new TextBlock { Text = "NAME", FontFamily = Theme.Mono, FontSize = 9.5, Foreground = Dim, Margin = new Thickness(0, 0, 0, 4) });
        var name = new TextBox
        {
            Text = _s.PetName, MaxLength = 12, FontFamily = Theme.Mono, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Brushes.White, CaretBrush = Brushes.White,
            BorderThickness = new Thickness(0), Padding = new Thickness(8, 6, 8, 6), Background = Brushes.Transparent,
        };
        name.TextChanged += (_, _) => { _s.PetName = string.IsNullOrWhiteSpace(name.Text) ? "Pixel" : name.Text.Trim(); Changed(); };
        name.LostFocus += (_, _) => _s.Save();
        side.Children.Add(new Border { Background = Field, BorderBrush = Theme.Res("LineHi"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), Child = name });

        side.Children.Add(new TextBlock { Text = "VORLAGEN", FontFamily = Theme.Mono, FontSize = 9.5, Foreground = Dim, Margin = new Thickness(0, 12, 0, 4) });
        var gallery = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4 };
        foreach (var (presetName, apply) in PixelPet.Presets)
        {
            var look = PixelPet.Copy(_s);
            apply(look);
            var tile = PictureTile(look, presetName, false, small: true);
            tile.MouseLeftButtonUp += (_, _) => { apply(_s); _s.Save(); Reload(); };
            gallery.Children.Add(tile);
        }
        side.Children.Add(gallery);

        var keys = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        void Quick(string text, Action act) { var b = Action(text, () => { act(); _s.Save(); Changed(); }); b.Margin = new Thickness(0, 0, 6, 6); keys.Children.Add(b); }
        var random = new Random();
        Quick("Zufall", () =>
        {
            _s.PetShape = random.Next(PixelPet.Shapes.Length); _s.PetEars = random.Next(PixelPet.EarKinds.Length); _s.PetEyes = random.Next(PixelPet.EyeKinds.Length);
            _s.PetMouth = random.Next(1, PixelPet.Mouths.Length); _s.PetPattern = random.Next(PixelPet.Patterns.Length); _s.PetHat = random.Next(PixelPet.Hats.Length);
            _s.PetAccessory = random.Next(PixelPet.Accessories.Length);
            _s.PetColor = PixelPet.Palette[random.Next(PixelPet.Palette.Length)];
            do _s.PetColor2 = PixelPet.Palette[random.Next(PixelPet.Palette.Length)]; while (_s.PetColor2 == _s.PetColor);
            _s.Save();
            Reload();
        });
        Quick("Antippen", preview.Poke);
        Quick("Standard", () => { PixelPet.Presets[1].Apply(_s); _s.Save(); Reload(); });
        side.Children.Add(keys);

        Page.Children.Add(tag);
        Page.Children.Add(top);

        // ---- parts as picture tiles: each shows your character with that option ----
        void Parts(string title, string[] names, Func<int> get, Action<Settings, int> set)
        {
            var section = Section(title);
            var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
            for (int i = 0; i < names.Length; i++)
            {
                int index = i;
                Border tile = null;
                void Draw()
                {
                    var look = PixelPet.Copy(_s);
                    set(look, index);
                    var fresh = PictureTile(look, names[index], get() == index);
                    var child = fresh.Child;
                    fresh.Child = null;
                    tile.Child = child;
                    tile.Background = fresh.Background;
                    tile.BorderBrush = fresh.BorderBrush;
                }
                tile = PictureTile(PixelPet.Copy(_s), names[i], false);
                tile.MouseLeftButtonUp += (_, _) => { set(_s, index); _s.Save(); Changed(); };
                tiles.Add(Draw);
                Draw();
                wrap.Children.Add(tile);
            }
            section.Children.Add(wrap);
        }
        Parts("Körper", PixelPet.Shapes, () => _s.PetShape, (s, i) => s.PetShape = i);
        Parts("Ohren", PixelPet.EarKinds, () => _s.PetEars, (s, i) => s.PetEars = i);
        Parts("Augen", PixelPet.EyeKinds, () => _s.PetEyes, (s, i) => s.PetEyes = i);
        Parts("Mund", PixelPet.Mouths, () => _s.PetMouth, (s, i) => s.PetMouth = i);
        Parts("Muster", PixelPet.Patterns, () => _s.PetPattern, (s, i) => s.PetPattern = i);
        Parts("Hut", PixelPet.Hats, () => _s.PetHat, (s, i) => s.PetHat = i);
        Parts("Accessoire", PixelPet.Accessories, () => _s.PetAccessory, (s, i) => s.PetAccessory = i);
        Parts("Schattierung", PixelPet.Shadings, () => _s.PetShading, (s, i) => s.PetShading = i);

        // ---- colors ----
        var colors = Section("Farben");
        Swatches(colors, "Körper", _s.PetColor, c => { _s.PetColor = c; Changed(); });
        Swatches(colors, "Zweitfarbe", _s.PetColor2, c => { _s.PetColor2 = c; Changed(); });
        Swatches(colors, "Augen", _s.PetEyeColor, c => { _s.PetEyeColor = c; Changed(); });
        Switch(colors, "Umriss", _s.PetOutline, v => { _s.PetOutline = v; Changed(); }, "Dunkle Kante wie in klassischer Pixel-Art.");
        Switch(colors, "Bäckchen", _s.PetCheeks, v => { _s.PetCheeks = v; Changed(); });

        // ---- effects ----
        var fx = Section("Effekte");
        var fxRow = Line("Aktiv", "Mehrere wählbar. Neu eingeschaltete zeigt die Vorschau sofort.");
        fxRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        fxRow.ColumnDefinitions.Add(new ColumnDefinition());
        var fxWrap = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
        foreach (var (effect, bit) in PixelPet.Effects)
        {
            var chip = Chip(effect, (_s.PetFx & (1 << bit)) != 0);
            chip.MouseLeftButtonUp += (_, _) =>
            {
                _s.PetFx ^= 1 << bit;
                SetChip(chip, (_s.PetFx & (1 << bit)) != 0);
                _s.Save();
                if ((_s.PetFx & (1 << bit)) != 0) preview.Poke();
                Changed();
            };
            fxWrap.Children.Add(chip);
        }
        Grid.SetColumn(fxWrap, 1);
        fxRow.Children.Add(fxWrap);
        fx.Children.Add(fxRow);

        // ---- behaviour ----
        var act = Section("Verhalten");
        Chips(act, "Tagesablauf", PixelPet.Behaviors, _s.PetBehavior, i => { _s.PetBehavior = i; Changed(); });
        Switch(act, "Tanzt zur Musik", _s.PetDance, v => { _s.PetDance = v; Changed(); }, "Hüpft im Takt, Noten und Aura gehen mit.");
        Switch(act, "In der kleinen Island", _s.PetInNotch, v => { _s.PetInNotch = v; App.Island?.PetChanged(); }, "Ohne Musik läuft es in der eingeklappten Island herum.");
        Slider(act, "Größe", 0.5, 1, () => Math.Min(1, _s.PetSize), v => { _s.PetSize = Math.Round(v, 2); Changed(); }, v => $"{v * 100:0} %");
        Slider(act, "Tempo", 0.3, 2.5, () => _s.PetSpeed, v => { _s.PetSpeed = Math.Round(v, 2); Changed(); }, v => $"{v * 100:0} %");
        Changed();
    }

    /// <summary>A tile with a small picture of a character look and its name; the chosen one is framed white.</summary>
    private static Border PictureTile(Settings look, string label, bool chosen, bool small = false)
    {
        var image = new Image { Source = PixelPet.Composite(look), Width = small ? 32 : 48, Height = small ? 40 : 60, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality); // shrunk tiles: averaging keeps the details
        var stack = new StackPanel();
        stack.Children.Add(image);
        stack.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(), FontFamily = Theme.Mono, FontSize = small ? 8 : 9, HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = small ? 48 : 70, Margin = new Thickness(0, 3, 0, 0),
            Foreground = chosen ? Brushes.White : (Brush)Application.Current.Resources["Dim"],
        });
        var tile = new Border
        {
            Width = small ? 52 : 78, Padding = new Thickness(2, 5, 2, 4), Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(2), Cursor = Cursors.Hand,
            BorderThickness = new Thickness(1), BorderBrush = chosen ? Brushes.White : Theme.Res("Line"),
            Background = chosen ? new SolidColorBrush(Color.FromArgb(0x1A, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(0x06, 255, 255, 255)),
            Child = stack, ToolTip = label,
        };
        Feedback.SetPress(tile, true);
        return tile;
    }

    private static Border Chip(string text, bool on)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(2), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 2, 4, 2), Cursor = Cursors.Hand,
            BorderBrush = Theme.Res("Line"), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = text.ToUpperInvariant(), FontFamily = Theme.Mono, FontSize = 10 },
        };
        Feedback.SetPress(chip, true);
        SetChip(chip, on);
        return chip;
    }

    private static void SetChip(Border chip, bool on)
    {
        chip.Background = on ? Selected : Brushes.Transparent;
        ((TextBlock)chip.Child).Foreground = on ? Ink : Brushes.White;
    }

    /// <summary>Like Pick, but the options wrap onto more lines, for long lists.</summary>
    private void Chips(StackPanel into, string label, string[] options, int selected, Action<int> apply)
    {
        var row = Line(label);
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var wrap = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
        for (int i = 0; i < options.Length; i++)
        {
            int index = i;
            var chip = Chip(options[i], i == selected);
            chip.MouseLeftButtonUp += (_, _) =>
            {
                foreach (Border c in wrap.Children) SetChip(c, c == chip);
                apply(index);
                _s.Save();
            };
            wrap.Children.Add(chip);
        }
        Grid.SetColumn(wrap, 1);
        row.Children.Add(wrap);
        into.Children.Add(row);
    }

    /// <summary>A row of color fields; the chosen one has a white frame.</summary>
    private void Swatches(StackPanel into, string label, string current, Action<string> apply, string[] palette = null)
    {
        var row = Line(label);
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var wrap = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
        foreach (var hex in palette ?? PixelPet.Palette)
        {
            var swatch = new Border
            {
                Width = 22, Height = 22, Margin = new Thickness(0, 2, 5, 2), CornerRadius = new CornerRadius(2), Cursor = Cursors.Hand, ToolTip = hex,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)), BorderThickness = new Thickness(2),
                BorderBrush = hex == current ? Brushes.White : Theme.Res("Line"),
            };
            Feedback.SetPress(swatch, true);
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                foreach (Border o in wrap.Children) o.BorderBrush = Theme.Res("Line");
                swatch.BorderBrush = Brushes.White;
                apply(hex);
                _s.Save();
            };
            wrap.Children.Add(swatch);
        }
        Grid.SetColumn(wrap, 1);
        row.Children.Add(wrap);
        into.Children.Add(row);
    }
    private void Effects()
    {
        var effects = Section("Island");
        Pick(effects, "Musik-Licht", new[] { "Aus", "Akzent", "Cover" }, _s.MusicGlow, i => { _s.MusicGlow = i; App.Island.ApplyGlow(); });
        Switch(effects, "Hitze-Alarm", _s.HeatAlarm, v => _s.HeatAlarm = v, "Die Island wird rot, wenn CPU oder GPU zu heiß werden.");
        Slider(effects, "Warnen ab", 70, 100, () => _s.HeatLimit, v => _s.HeatLimit = Math.Round(v), v => $"{v:0} °C");

    }
}
