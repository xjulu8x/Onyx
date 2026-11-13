using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DesktopOverlay;

/// <summary>Analog clock: dotted dial, plain numbers, two hands and a thin red second hand.</summary>
public sealed class ClockCard : Card
{
    private const double Size = 166;
    private readonly RotateTransform _hour = new(), _minute = new(), _second = new();

    public ClockCard() : base("Clock", "", 196, 196)
    {
        HideHeader();
        var dial = new Canvas { Width = Size, Height = Size };
        double c = Size / 2;
        for (int i = 0; i < 60; i++)
        {
            bool hour = i % 5 == 0;
            double a = i * Math.PI / 30, r = c - 3, d = hour ? 3 : 1.6;
            var dot = new Rectangle { Width = d, Height = d, Opacity = hour ? 0.9 : 0.4 };
            dot.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = this });
            Canvas.SetLeft(dot, c + r * Math.Sin(a) - d / 2);
            Canvas.SetTop(dot, c - r * Math.Cos(a) - d / 2);
            dial.Children.Add(dot);
        }
        for (int n = 1; n <= 12; n++)
        {
            double a = n * Math.PI / 6, r = c - 20;
            var label = new TextBlock { Text = n.ToString(), FontSize = 15, FontWeight = FontWeights.SemiBold, Width = 24, TextAlignment = TextAlignment.Center };
            Canvas.SetLeft(label, c + r * Math.Sin(a) - 12);
            Canvas.SetTop(label, c - r * Math.Cos(a) - 10);
            dial.Children.Add(label);
        }
        Rectangle Hand(double width, double length, RotateTransform turn, Brush fill = null)
        {
            var hand = new Rectangle { Width = width, Height = length + 8, RenderTransform = turn, RenderTransformOrigin = new Point(0.5, length / (length + 8)) };
            if (fill != null) hand.Fill = fill;
            else hand.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = this });
            Canvas.SetLeft(hand, c - width / 2);
            Canvas.SetTop(hand, c - length);
            dial.Children.Add(hand);
            return hand;
        }
        Hand(4, 42, _hour);
        Hand(2.5, 62, _minute);
        Hand(1, 66, _second, Theme.Res("Signal"));
        var pin = new Rectangle { Width = 6, Height = 6, Fill = Theme.Res("Signal") };
        Canvas.SetLeft(pin, c - 3);
        Canvas.SetTop(pin, c - 3);
        dial.Children.Add(pin);
        SetBody(dial);
        Every(TimeSpan.FromSeconds(1), Tick);
        Tick();
    }

    private void Tick()
    {
        var now = DateTime.Now;
        _second.Angle = now.Second * 6;
        _minute.Angle = now.Minute * 6 + now.Second * 0.1;
        _hour.Angle = now.Hour % 12 * 30 + now.Minute * 0.5;
    }

    protected override void OnResume() => Tick();

    protected override Point DefaultPlace(Rect w) => new(w.Right - 494, w.Top + 256);
}

/// <summary>Month view: today inverted, the days of the neighbouring months faint, arrows to page through.</summary>
public sealed class CalendarCard : Card
{
    private static readonly CultureInfo De = new("de-DE");
    private readonly UniformGrid _days = new() { Columns = 7 };
    private DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _built;

    public CalendarCard() : base("Calendar", "", 236, 200)
    {
        Button Arrow(string glyph, int step)
        {
            var b = new Button { Style = (Style)Application.Current.Resources["IconButton"], Content = glyph, FontSize = 9, Padding = new Thickness(5, 2, 5, 2) };
            b.Click += (_, _) => { _month = _month.AddMonths(step); Build(); };
            return b;
        }
        Meta.Children.Add(Arrow("", -1));
        Meta.Children.Add(Arrow("", 1));
        Caption.MouseLeftButtonUp += (_, _) => { _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); Build(); };
        SetBody(_days);
        Every(TimeSpan.FromMinutes(1), () => { if (_built != DateTime.Today) Build(); });
        Build();
    }

    private void Build()
    {
        _built = DateTime.Today;
        Caption.Text = _month.ToString("MMMM yyyy", De).ToUpperInvariant();
        _days.Children.Clear();
        foreach (var d in new[] { "MO", "DI", "MI", "DO", "FR", "SA", "SO" })
            _days.Children.Add(new TextBlock { Text = d, FontFamily = Theme.Mono, FontSize = 8.5, Foreground = Theme.Res("Dim"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 3) });
        int offset = ((int)_month.DayOfWeek + 6) % 7;
        var start = _month.AddDays(-offset);
        for (int i = 0; i < 42; i++)
        {
            var day = start.AddDays(i);
            bool today = day == DateTime.Today, inMonth = day.Month == _month.Month;
            var text = new TextBlock
            {
                Text = day.Day.ToString(), FontFamily = Theme.Mono, FontSize = 9.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                FontWeight = today ? FontWeights.Bold : FontWeights.Normal, Opacity = inMonth ? 1 : 0.35,
            };
            if (today) text.Foreground = Theme.Res("Ink");
            var cell = new Border { Height = 19, Width = 26, CornerRadius = new CornerRadius(2), Child = text };
            if (today) cell.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = this });
            _days.Children.Add(cell);
        }
    }

    protected override Point DefaultPlace(Rect w) => new(w.Right - 514, w.Top + 44);
}

/// <summary>
/// Weather from Open-Meteo (free, no account). Only the city typed in the settings is sent there, to find its coordinates.
/// The picture is ASCII art, like in a terminal.
/// </summary>
public sealed class WeatherCard : Card
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly TextBlock _temp = new() { FontSize = 40, FontWeight = FontWeights.SemiBold, Text = "--°" };
    private readonly TextBlock _art = Screen(8.5);
    private readonly TextBlock _state = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBlock _range = Mono("", 9.5, 0.8);
    private readonly TextBlock _stamp = Mono("", 8.5, 0.5);
    private DateTime _fetched;

    public WeatherCard() : base("Weather", "Wetter", 196, 200)
    {
        var pin = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.Resources["Icons"], FontSize = 10, Foreground = Theme.Res("Dim"), Cursor = Cursors.Hand, ToolTip = "Ort in den Einstellungen festlegen" };
        pin.MouseLeftButtonUp += (_, _) => ((App)Application.Current).OpenSettings();
        Meta.Children.Add(pin);

        var top = new Grid();
        top.Children.Add(_temp);
        _art.HorizontalAlignment = HorizontalAlignment.Right;
        _art.VerticalAlignment = VerticalAlignment.Center;
        _art.Opacity = 0.85;
        top.Children.Add(_art);
        var stack = new StackPanel();
        stack.Children.Add(top);
        stack.Children.Add(_state);
        stack.Children.Add(_range);
        _stamp.Margin = new Thickness(0, 14, 0, 0);
        _stamp.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(_stamp);
        SetBody(stack);
        Every(TimeSpan.FromMinutes(30), Fetch);
        Loaded += (_, _) => Fetch();
    }

    protected override void OnResume()
    {
        if ((DateTime.Now - _fetched).TotalMinutes > 30) Fetch();
    }

    /// <summary>Called by the settings after the city changed.</summary>
    public void Refresh() { _fetched = default; Fetch(); }

    private async void Fetch()
    {
        var s = Settings.Current;
        if (string.IsNullOrWhiteSpace(s.WeatherCity))
        {
            Caption.Text = "WETTER";
            _temp.Text = "--°";
            _art.Text = Art(-1);
            _state.Text = "Kein Ort";
            _range.Text = "ZAHNRAD > WIDGETS > ORT";
            _stamp.Text = "";
            return;
        }
        _fetched = DateTime.Now;
        try
        {
            if (s.WeatherPlace is not { Length: 2 })
            {
                var url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=de&name=" + Uri.EscapeDataString(s.WeatherCity.Trim());
                using var geo = JsonDocument.Parse(await Http.GetStringAsync(url));
                if (!geo.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                {
                    _state.Text = "Ort nicht gefunden";
                    _range.Text = s.WeatherCity.ToUpperInvariant();
                    return;
                }
                var place = results[0];
                s.WeatherPlace = new[] { place.GetProperty("latitude").GetDouble(), place.GetProperty("longitude").GetDouble() };
                s.Save();
            }
            var inv = CultureInfo.InvariantCulture;
            var forecast = $"https://api.open-meteo.com/v1/forecast?latitude={s.WeatherPlace[0].ToString(inv)}&longitude={s.WeatherPlace[1].ToString(inv)}" +
                           "&current=temperature_2m,weather_code&daily=temperature_2m_max,temperature_2m_min&forecast_days=1&timezone=auto";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(forecast));
            var current = doc.RootElement.GetProperty("current");
            var daily = doc.RootElement.GetProperty("daily");
            int code = current.GetProperty("weather_code").GetInt32();
            Caption.Text = s.WeatherCity.Trim().ToUpperInvariant();
            _temp.Text = $"{Math.Round(current.GetProperty("temperature_2m").GetDouble()):0}°";
            _art.Text = Art(code);
            _state.Text = Describe(code);
            _range.Text = $"MAX {Math.Round(daily.GetProperty("temperature_2m_max")[0].GetDouble()):0}°  MIN {Math.Round(daily.GetProperty("temperature_2m_min")[0].GetDouble()):0}°";
            _stamp.Text = $"AKTUALISIERT {DateTime.Now:HH:mm}\nWETTER VON OPEN-METEO";
        }
        catch
        {
            _state.Text = "Keine Verbindung";
            _stamp.Text = $"VERSUCH {DateTime.Now:HH:mm}";
            _fetched = DateTime.Now.AddMinutes(-25); // try again in about five minutes
        }
    }

    private static string Describe(int code) => code switch
    {
        0 => "Klar", 1 => "Überwiegend klar", 2 => "Teils bewölkt", 3 => "Bedeckt",
        45 or 48 => "Nebel", >= 51 and <= 57 => "Niesel", >= 61 and <= 67 => "Regen", >= 71 and <= 77 => "Schnee",
        >= 80 and <= 82 => "Schauer", 85 or 86 => "Schneeschauer", >= 95 => "Gewitter", _ => "Wechselhaft",
    };

    /// <summary>Little weather pictures in the style of terminal weather tools.</summary>
    private static string Art(int code) => code switch
    {
        0 or 1 => " \\ | / \n- ( ) -\n / | \\ ",
        2 => " \\  .-. \n-( (   ).\n  (___(__)",
        3 or 45 or 48 => "   .--.  \n .(    ). \n(___.__)_)",
        >= 71 and <= 77 or 85 or 86 => "   .--.  \n (___.__) \n  * * * * ",
        >= 95 => "   .--.  \n (___.__) \n  /_ /_   ",
        >= 51 => "   .--.  \n (___.__) \n  ' ' ' ' ",
        _ => "   .--.  \n (  ?   ) \n (___.__) ",
    };

    protected override Point DefaultPlace(Rect w) => new(w.Right - 722, w.Top + 44);
}

/// <summary>Music: cover, title, keys, progress and a dot-matrix level meter that runs only while music plays.</summary>
public sealed class MusicCard : Card
{
    private readonly MediaService _media = MediaService.Instance;
    private readonly Border _cover = new() { Width = 64, Height = 64, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(0x1A, 255, 255, 255)) };
    private readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _artist = new() { FontSize = 11, FontWeight = FontWeights.SemiBold, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) };
    private readonly Button _play;
    private readonly Border _track = new() { Height = 2, Background = Theme.Res("Track") };
    private readonly Border _fill = new() { Height = 2, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Rectangle _knob = new() { Width = 6, Height = 6, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _pos = Mono("0:00", 9, 0.6), _dur = Mono("0:00", 9, 0.6);
    private readonly DotMeter _meter = new() { Height = 34, Margin = new Thickness(0, 12, 0, 0) };

    public MusicCard() : base("Music", "Musik", 340, 250)
    {
        Meta.Children.Add(new TextBlock { Text = "●", Foreground = Theme.Res("Signal"), FontSize = 7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
        Meta.Children.Add(Theme.Tag("Live"));
        foreach (var bind in new FrameworkElement[] { _fill, _knob })
            bind.SetBinding(bind is Shape ? Shape.FillProperty : Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = this });

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.Children.Add(_cover);
        var names = new StackPanel { Margin = new Thickness(14, 6, 0, 0) };
        names.Children.Add(_title);
        names.Children.Add(_artist);
        Grid.SetColumn(names, 1);
        head.Children.Add(names);

        Button Control(string glyph, double size, Action click)
        {
            var b = new Button { Style = (Style)Application.Current.Resources["IconButton"], Content = glyph, FontSize = size, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(6, 0, 6, 0) };
            b.Click += (_, _) => click();
            return b;
        }
        var keys = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 6) };
        keys.Children.Add(Control("", 12, _media.Previous));
        _play = Control("", 14, _media.PlayPause);
        _play.BorderBrush = Theme.Res("LineHi");
        keys.Children.Add(_play);
        keys.Children.Add(Control("", 12, _media.Next));

        var bar = new Grid { Margin = new Thickness(0, 2, 0, 0), Height = 8 };
        _track.VerticalAlignment = _fill.VerticalAlignment = _knob.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(_track);
        bar.Children.Add(_fill);
        bar.Children.Add(_knob);
        var times = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        _dur.HorizontalAlignment = HorizontalAlignment.Right;
        times.Children.Add(_pos);
        times.Children.Add(_dur);

        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(keys);
        stack.Children.Add(bar);
        stack.Children.Add(times);
        stack.Children.Add(_meter);
        SetBody(stack);
        _media.Updated += () => Dispatcher.BeginInvoke(Show);
        Every(TimeSpan.FromMilliseconds(83), Tick); // 12 frames per second, only while visible
        Show();
    }

    private void Show()
    {
        bool has = _media.HasSession;
        Caption.Text = has && _media.Source.Length > 0 ? "♫ " + _media.Source.ToUpperInvariant() : "♫ MUSIK";
        _title.Text = has ? _media.Title : "Nichts wird abgespielt";
        _artist.Text = has ? _media.Artist : "";
        _play.Content = _media.IsPlaying ? "" : "";
        _cover.Background = has && _media.Art != null ? new ImageBrush(_media.Art) { Stretch = Stretch.UniformToFill } : new SolidColorBrush(Color.FromArgb(0x1A, 255, 255, 255));
        Progress();
    }

    private int _frame;

    private void Tick()
    {
        // The level meter moves with the music; the progress line only needs a step a second.
        bool playing = _media.IsPlaying;
        _meter.Push(playing ? Volume.Peak() : 0, playing);
        if (++_frame % 12 == 0) Progress();
    }

    private void Progress()
    {
        double total = _media.Duration.TotalSeconds, at = Math.Min(_media.Position.TotalSeconds, total);
        double width = Math.Max(0, Frame.Width - 30);
        double share = total > 0 ? at / total : 0;
        _fill.Width = width * share;
        _knob.Margin = new Thickness(Math.Max(0, width * share - 3), 0, 0, 0);
        _pos.Text = MediaService.Fmt(TimeSpan.FromSeconds(at));
        _dur.Text = MediaService.Fmt(_media.Duration);
    }

    protected override Point DefaultPlace(Rect w) => new(w.Left + 16, w.Top + 44);
}

/// <summary>A strip of small squares that scrolls the recent output level from right to left, like an LED display.</summary>
public sealed class DotMeter : FrameworkElement
{
    private const double Cell = 5, Gap = 2;
    private readonly List<double> _levels = new();
    private bool _idleDrawn;
    private static readonly Brush Off = Freeze(new SolidColorBrush(Color.FromArgb(0x16, 255, 255, 255)));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    public void Push(double level, bool playing)
    {
        if (!playing)
        {
            if (_idleDrawn) return; // nothing moves while paused, so nothing is redrawn
            _levels.Clear();
            _idleDrawn = true;
            InvalidateVisual();
            return;
        }
        _idleDrawn = false;
        _levels.Add(Math.Sqrt(Math.Clamp(level, 0, 1)));
        int columns = (int)(ActualWidth / (Cell + Gap));
        while (_levels.Count > Math.Max(1, columns)) _levels.RemoveAt(0);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        int columns = (int)(ActualWidth / (Cell + Gap)), rows = (int)((ActualHeight + Gap) / (Cell + Gap));
        var on = (Brush)GetValue(TextElement.ForegroundProperty) ?? Brushes.White;
        int start = columns - _levels.Count;
        for (int col = 0; col < columns; col++)
        {
            double level = col >= start ? _levels[col - start] : 0;
            int lit = (int)Math.Round(level * rows);
            for (int row = 0; row < rows; row++)
            {
                bool isOn = rows - row <= lit;
                dc.DrawRectangle(isOn ? on : Off, null, new Rect(col * (Cell + Gap), row * (Cell + Gap), Cell, Cell));
            }
        }
    }
}
