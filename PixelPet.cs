using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopOverlay;

/// <summary>
/// The pixel character of the island. It is built from parts (body, ears, eyes, mouth, pattern, hat) on a 32×40 grid with
/// light, shade and outline, drawn once per pose into a small bitmap and scaled up sharply. It wanders, rests, naps,
/// dances to music, hops when clicked and has a set of effects. It only moves while it can be seen.
/// </summary>
public sealed class PixelPet : FrameworkElement
{
    public static readonly string[] Shapes = { "Rund", "Eckig", "Hoch", "Geist", "Bohne" };
    public static readonly string[] EarKinds = { "Keine", "Katze", "Hase", "Bär", "Antenne", "Hörner", "Fuchs" };
    public static readonly string[] EyeKinds = { "Punkte", "Groß", "Müde", "Fröhlich", "Visier", "Sterne" };
    public static readonly string[] Mouths = { "Keiner", "Lächeln", ":3", "Zähnchen", "Staunen", "Zunge", "Schnabel" };
    public static readonly string[] Patterns = { "Keins", "Bauch", "Streifen", "Flecken", "Zweifarbig", "Sternchen", "Maske" };
    public static readonly string[] Hats = { "Keiner", "Kappe", "Krone", "Kopfhörer", "Mütze", "Heiligenschein", "Blume", "Schleife", "Zauberhut" };
    public static readonly string[] Accessories = { "Keins", "Brille", "Sonnenbrille", "Schal", "Fliege", "Glocke" };
    public static readonly string[] Shadings = { "Flach", "Weich", "Stark" };

    /// <summary>Ready-made characters; each sets every part and color.</summary>
    public static readonly (string Name, Action<Settings> Apply)[] Presets =
    {
        ("Pinguin", s => Set(s, 0, 0, 0, 6, 6, 0, 0, "#22252B", "#FAF7F0", "#14141A")),
        ("Katze", s => Set(s, 0, 1, 1, 2, 1, 0, 5, "#E6E6E6", "#FFB340", "#14141A")),
        ("Fuchs", s => Set(s, 4, 6, 3, 1, 1, 0, 3, "#FF7A45", "#FAF7F0", "#14141A")),
        ("Hase", s => Set(s, 2, 2, 0, 2, 1, 7, 0, "#F2E3C6", "#FF6B8A", "#14141A")),
        ("Bär", s => Set(s, 0, 3, 0, 1, 1, 0, 0, "#8B5E3C", "#F2E3C6", "#14141A")),
        ("Geist", s => Set(s, 3, 0, 1, 4, 0, 5, 0, "#E6E6E6", "#C58BFF", "#14141A")),
        ("Bot", s => Set(s, 1, 4, 4, 0, 0, 0, 0, "#9A9AA2", "#4FD1C5", "#14141A")),
        ("Frosch", s => Set(s, 4, 0, 1, 1, 1, 2, 0, "#30D158", "#F2E3C6", "#14141A")),
    };

    private static void Set(Settings s, int shape, int ears, int eyes, int mouth, int pattern, int hat, int accessory, string color, string color2, string eye)
    {
        (s.PetShape, s.PetEars, s.PetEyes, s.PetMouth, s.PetPattern, s.PetHat, s.PetAccessory) = (shape, ears, eyes, mouth, pattern, hat, accessory);
        (s.PetColor, s.PetColor2, s.PetEyeColor) = (color, color2, eye);
    }

    /// <summary>A copy of the character settings only, to draw a variant without touching the real ones.</summary>
    public static Settings Copy(Settings from)
    {
        var to = new Settings();
        foreach (var prop in typeof(Settings).GetProperties().Where(p => p.Name.StartsWith("Pet") && p.CanWrite)) prop.SetValue(to, prop.GetValue(from));
        return to;
    }

    public static readonly string[] Behaviors = { "Läuft herum", "Bleibt stehen", "Schläft" };
    public static readonly (string Name, int Bit)[] Effects =
    {
        ("Noten", 0), ("Herzen", 1), ("Funkeln", 2), ("Staub", 3), ("Zzz", 4), ("Aura", 5), ("Regenbogen", 6), ("Konfetti", 7), ("Schweiß", 8),
    };
    public static readonly string[] Palette =
    {
        "#E6E6E6", "#9A9AA2", "#3A3A42", "#F2E3C6", "#8B5E3C", "#FFB340", "#FF7A45", "#FF453A",
        "#FF6B8A", "#C58BFF", "#7FA6F0", "#4FD1C5", "#30D158", "#14141A", "#FAF7F0", "#22252B",
    };

    private const int W = 32, H = 40, OY = 8; // grid; the body starts 8 rows down to leave room for ears and hats

    /// <summary>Small version for the notch: one screen pixel per grid pixel, slower frames.</summary>
    public bool Compact { get; set; }

    /// <summary>Extra magnification, for the big preview in the settings.</summary>
    public double Zoom { get; set; } = 1;

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
    private readonly Random _random = new();
    private readonly List<Bit> _bits = new();
    private readonly List<(double X, double Life)> _trail = new();
    private double _x = 20, _target = 20, _vel, _stride, _hop, _hopSpeed, _squash = 1, _aura;
    private int _frame, _blink, _facing = 1, _left = 20, _armBeat;
    private bool _wantRun;

    /// <summary>What the character is doing; each lasts a while, then the next is picked like a little daily routine.</summary>
    private enum Act { Idle, Walk, Look, Sit, Yawn, Sleep }
    private Act _act = Act.Idle;
    private int _actLength = 1;
    private bool _watching;

    private bool Sleeping => _act == Act.Sleep;

    private sealed class Bit
    {
        public string Kind; public double X, Y, Vx, Vy, Life; public Color Color;
    }

    public PixelPet()
    {
        _timer.Tick += (_, _) => Step();
        Cursor = Cursors.Hand;
        MouseLeftButtonUp += (_, _) => Poke();
        IsVisibleChanged += (_, _) => Apply();
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor); // hard pixel edges when scaled up
    }

    public bool Running { set { _wantRun = value; Apply(); } }

    private double Fps => Compact ? 6 : 11;

    private void Apply()
    {
        _timer.Interval = TimeSpan.FromMilliseconds(1000 / Fps);
        _timer.IsEnabled = _wantRun && IsVisible;
        InvalidateVisual();
    }

    private int Frames(double seconds) => Math.Max(1, (int)(seconds * Fps));

    /// <summary>A click: wakes up, hops and shows hearts and confetti.</summary>
    public void Poke()
    {
        Start(Act.Idle, Frames(2));
        if (_hop <= 0) _hopSpeed = Compact ? 2.6 : 6.5;
        double p = Pixel, head = ActualHeight - VisibleRows * p;
        if (On(1)) for (int i = 0; i < 3; i++) Add("heart", _x + W * p / 2 + (i - 1) * 6 * p, head, (i - 1) * 0.4, -0.9, Color.FromRgb(0xFF, 0x6B, 0x8A));
        if (On(7)) for (int i = 0; i < 14; i++) Add("confetti", _x + W * p / 2, head + 6 * p, (_random.NextDouble() - 0.5) * 4, -2 - _random.NextDouble() * 2.5, Rainbow(_random.Next(6)));
    }

    private static bool On(int bit) => (Settings.Current.PetFx & (1 << bit)) != 0;

    /// <summary>Rows of the sprite that hold something: the empty space above ears and hats doesn't count, so the character fills its place.</summary>
    private static int VisibleRows => H - ContentTop(Settings.Current);

    /// <summary>
    /// Size of one sprite pixel on screen. The character fills the height it has (the "Größe" setting scales that),
    /// in the notch as large as the pill allows.
    /// </summary>
    private double Pixel
    {
        get
        {
            double room = Math.Max(10, (ActualHeight > 0 ? ActualHeight : Compact ? 32 : 140 * Zoom) - (Compact ? 1 : 22)); // in the card: room for the caption above
            double fit = room / VisibleRows;
            if (Compact) return fit;
            double p = fit * Math.Clamp(Settings.Current.PetSize, 0.5, 1); // 100 % and more fill the card
            p = Math.Min(p, fit);
            return Math.Max(1, Math.Floor(p * 2) / 2); // half-pixel steps stay sharp
        }
    }

    protected override Size MeasureOverride(Size available) => new(Math.Min(available.Width, 600), Compact ? 32 : 140 * Zoom);

    private void Add(string kind, double x, double y, double vx, double vy, Color color) =>
        _bits.Add(new Bit { Kind = kind, X = x, Y = y, Vx = vx, Vy = vy, Life = 1, Color = color });

    private static Color Rainbow(int i) => i switch
    {
        0 => Color.FromRgb(0xFF, 0x45, 0x3A), 1 => Color.FromRgb(0xFF, 0x9F, 0x0A), 2 => Color.FromRgb(0xFF, 0xD6, 0x0A),
        3 => Color.FromRgb(0x30, 0xD1, 0x58), 4 => Color.FromRgb(0x0A, 0x84, 0xFF), _ => Color.FromRgb(0xBF, 0x5A, 0xF2),
    };

    // ---------- behaviour ----------

    private void Start(Act act, int frames)
    {
        _act = act;
        _left = _actLength = frames;
    }

    /// <summary>The next thing to do. Walks are common, rests in between; at night it naps more often and longer.</summary>
    private void Next()
    {
        var s = Settings.Current;
        int hour = DateTime.Now.Hour;
        bool night = hour >= 23 || hour < 6;
        if (s.PetBehavior == 2) { Start(Act.Sleep, Frames(600)); return; }
        double r = _random.NextDouble();
        if (s.PetBehavior == 1)
        {
            if (r < 0.45) Start(Act.Idle, Frames(2 + _random.NextDouble() * 3));
            else if (r < 0.7) Start(Act.Look, Frames(2 + _random.NextDouble() * 2));
            else if (r < 0.9) Start(Act.Sit, Frames(5 + _random.NextDouble() * 8));
            else Start(Act.Yawn, Frames(1.6));
            return;
        }
        double sleep = night ? 0.3 : 0.07;
        if (r < sleep) Start(Act.Sleep, Frames(night ? 60 + _random.Next(120) : 15 + _random.Next(30)));
        else if (r < sleep + 0.42)
        {
            // A real destination: not just a step to the side.
            double floor = Math.Max(1, ActualWidth - W * Pixel);
            for (int i = 0; i < 6; i++) { _target = _random.NextDouble() * floor; if (Math.Abs(_target - _x) > floor * 0.25) break; }
            Start(Act.Walk, Frames(30));
        }
        else if (r < sleep + 0.62) Start(Act.Idle, Frames(1.5 + _random.NextDouble() * 3));
        else if (r < sleep + 0.78) Start(Act.Look, Frames(1.8 + _random.NextDouble() * 1.5));
        else if (r < sleep + 0.92) Start(Act.Sit, Frames(4 + _random.NextDouble() * 8));
        else Start(Act.Yawn, Frames(1.6));
    }

    private void Step()
    {
        _frame++;
        var s = Settings.Current;
        double p = Pixel;
        double floor = Math.Max(0, ActualWidth - W * p);
        double head = ActualHeight - VisibleRows * p;
        bool music = MediaService.Instance.IsPlaying && s.PetDance;
        double level = music ? Math.Sqrt(Volume.Peak()) : 0;

        if (music && !Compact)
        {
            // Dance: hops on loud beats, arms up in turn, notes and a pulsing aura.
            if (_act == Act.Sleep) Start(Act.Idle, 1);
            _vel *= 0.6;
            if (_hop <= 0 && level > 0.4) { _hopSpeed = 2.5 + level * 4; _armBeat++; }
            if (On(0) && _frame % 7 == 0) Add("note", _x + _random.Next(4, 28) * p, head, (_random.NextDouble() - 0.5) * 0.6, -0.9, Settings.ToColor(s.PetColor2, 0xFF, Colors.White));
            if (_frame % 30 == 0) _facing = -_facing;
        }
        else
        {
            if (music && Compact && _hop <= 0 && level > 0.5 && _act != Act.Walk) _hopSpeed = 1.1; // nods to the music in the notch
            if (--_left <= 0 && _act != Act.Walk) Next();
            switch (_act)
            {
                case Act.Walk:
                    // Starts slowly, keeps a pace, slows down before it stops: no robot steps.
                    double max = Math.Clamp(s.PetSpeed, 0.3, 2.5) * (Compact ? 0.9 : 1.8) * Math.Max(1, p / 3);
                    double d = _target - _x;
                    double want = Math.Sign(d) * Math.Min(max, Math.Abs(d) * 0.12 + 0.15);
                    _vel += (want - _vel) * 0.3;
                    if (Math.Abs(d) < 0.6 && Math.Abs(_vel) < 0.2) { _vel = 0; Start(Act.Idle, Frames(1 + _random.NextDouble() * 2)); }
                    if (s.PetBehavior != 0) Start(Act.Idle, 1);
                    break;
                case Act.Look:
                    if (_left % Frames(0.9) == 0) _facing = -_facing; // looks left and right
                    _vel *= 0.5;
                    break;
                default:
                    _vel *= 0.5;
                    break;
            }
        }
        if (Math.Abs(_vel) > 0.05)
        {
            _x += _vel;
            _stride += Math.Abs(_vel);
            _facing = Math.Sign(_vel);
            if (On(3) && !Compact && _hop <= 0 && (int)(_stride / (6 * p)) != (int)((_stride - Math.Abs(_vel)) / (6 * p)))
                Add("dust", _x + (_facing > 0 ? 8 : W - 8) * p, ActualHeight - 3 * p, -_facing * 0.3, -0.2, Color.FromRgb(0x8A, 0x8A, 0x8A));
            if (On(6) && !Compact) _trail.Add((_x, 1));
        }
        if (_x < 0 || _x > floor) { _x = Math.Clamp(_x, 0, floor); _vel = 0; if (_act == Act.Walk) Start(Act.Look, Frames(1.5)); }
        _target = Math.Clamp(_target, 0, floor);

        // Looks at the mouse when it comes close, unless asleep or busy walking.
        if (_frame % 3 == 0) _watching = false;
        if (_frame % 3 == 0 && !Sleeping && _act != Act.Walk && IsVisible && Native.GetCursorPos(out var cursor))
        {
            var me = PointToScreen(new Point(_x + W * p / 2, ActualHeight - VisibleRows * p / 2));
            double dx = cursor.X - me.X, dy = cursor.Y - me.Y;
            if (Math.Abs(dx) < 260 && Math.Abs(dy) < 160 && Math.Abs(dx) > 6) { _facing = Math.Sign(dx); _watching = true; }
        }

        // Hop physics with squash on landing.
        if (_hopSpeed > 0 || _hop > 0)
        {
            _hop += _hopSpeed;
            _hopSpeed -= Compact ? 0.7 : 1.4;
            if (_hop <= 0) { _hop = 0; _hopSpeed = 0; _squash = 0.84; }
        }
        _squash += (1 - _squash) * 0.35;
        if (_hop > 0) _squash = 1.05;

        if (!Sleeping && --_blink < -Frames(2.5) - _random.Next(Frames(4))) _blink = Compact ? 1 : 2;
        _aura = music ? Math.Max(_aura * 0.85, level) : _aura * 0.9;

        // Ambient effects.
        if (Sleeping && On(4) && _frame % Frames(1.3) == 0) Add("z", _x + (_facing > 0 ? 22 : 8) * p, head, 0.25, -0.5, Color.FromRgb(0xC8, 0xC8, 0xD0));
        if (!Sleeping && On(2) && !Compact && _random.NextDouble() < 0.05) Add("spark", _x + _random.Next(-4, W + 4) * p, head + _random.Next(0, VisibleRows) * p, 0, 0, Color.FromRgb(0xFF, 0xE0, 0x8A));
        if (On(8) && !Compact && _frame % 9 == 0 && (SystemMonitor.Instance.CpuTemp > 80 || SystemMonitor.Instance.Cpu > 85))
            Add("sweat", _x + (_facing > 0 ? 25 : 7) * p, head + 4 * p, _facing * 0.2, 0, Color.FromRgb(0x7F, 0xD0, 0xFF));

        foreach (var b in _bits)
        {
            b.X += b.Vx; b.Y += b.Vy;
            if (b.Kind is "confetti" or "sweat") b.Vy += 0.25;
            if (b.Kind is "note" or "heart" or "z") b.X += Math.Sin(b.Life * 8) * 0.3;
            b.Life -= (b.Kind switch { "spark" => 0.12, "dust" => 0.1, "confetti" => 0.035, _ => 0.045 }) * 11 / Fps;
        }
        _bits.RemoveAll(b => b.Life <= 0 || b.Y > ActualHeight + 4 || b.Y < -20);
        for (int i = _trail.Count - 1; i >= 0; i--) { var t = _trail[i]; t.Life -= 0.08; if (t.Life <= 0) _trail.RemoveAt(i); else _trail[i] = t; }

        // Redraw only when the picture changes, so resting costs next to nothing (most of all in the notch).
        _pose = CurrentPose(music && !Compact);
        var look = (Math.Round(_x), Math.Round(_hop), _bits.Count, _facing, Math.Round(_squash, 2), _trail.Count, _pose, Sleeping ? _frame / Frames(1) : 0);
        if (look == _lastLook && _bits.Count == 0) return;
        _lastLook = look;
        InvalidateVisual();
    }

    private Pose _pose;
    private (double, double, int, int, double, int, Pose, int) _lastLook;

    private Pose CurrentPose(bool dancing)
    {
        double p = Pixel;
        bool walking = Math.Abs(_vel) > 0.05;
        bool yawning = _act == Act.Yawn && _left > _actLength * 0.3;
        return new Pose(
            Step: walking && (int)(_stride / (3.5 * p)) % 2 == 1,
            Closed: Sleeping || _blink > 0 || yawning,
            ArmsUp: dancing ? _armBeat % 2 == 0 : yawning,
            Open: dancing || yawning,
            Wave: _frame / Frames(0.4) % 2,
            Sit: (_act == Act.Sit || Sleeping) && !walking && !dancing);
    }

    // ---------- drawing ----------

    protected override void OnRender(DrawingContext dc)
    {
        var s = Settings.Current;
        double p = Pixel;
        double baseY = ActualHeight - VisibleRows * p;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // whole area clickable

        if (!Compact)
        {
            // Floor of dots, like the dot grid of the rest of Onyx.
            var dot = Frozen(Color.FromArgb(0x40, 255, 255, 255));
            for (double x = 0; x < ActualWidth; x += 8) dc.DrawRectangle(dot, null, new Rect(x, ActualHeight - 1, 2, 1));
        }

        // Rainbow trail behind the character.
        foreach (var (x, life) in _trail)
            for (int i = 0; i < 6; i++)
                dc.DrawRectangle(Frozen(Rainbow(i), life * 0.7), null, new Rect(x + (_facing > 0 ? 4 : W - 6) * p, ActualHeight - (10 - i) * p, 2 * p, p));

        // Aura: a soft glow in the second color, pulsing with the music.
        if (On(5) && !Compact)
        {
            var c = Settings.ToColor(s.PetColor2, 0xFF, Colors.White);
            double a = 0.12 + _aura * 0.35;
            var glow = new RadialGradientBrush(Color.FromArgb((byte)(255 * a), c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B));
            glow.Freeze();
            dc.DrawEllipse(glow, null, new Point(_x + W * p / 2, baseY + VisibleRows * p * 0.55 - _hop), W * p * 0.75, VisibleRows * p * 0.55);
        }

        // Shadow, smaller while in the air.
        double shadow = Math.Max(3, 10 - _hop / (2 * p)) * p;
        if (!Compact) dc.DrawRectangle(Frozen(Color.FromArgb(0x30, 255, 255, 255)), null, new Rect(_x + W * p / 2 - shadow, ActualHeight - 2, shadow * 2, Math.Max(1, p * 0.75)));

        var image = Sprite(s, _pose);
        double sink = _pose.Sit ? 2 * p : 0; // sitting: the body rests lower, feet tucked away
        double cx = _x + W * p / 2, bottom = ActualHeight - (Compact ? 0 : 1) - _hop + sink;
        double breathe = Sleeping ? 1 + Math.Sin(_frame * 2.5 / Fps) * 0.03 : 1;
        dc.PushTransform(new TranslateTransform(cx, bottom));
        dc.PushTransform(new ScaleTransform(_facing * (2 - _squash), _squash * breathe));
        dc.DrawImage(image, new Rect(-W * p / 2, -H * p, W * p, H * p));
        dc.Pop();
        dc.Pop();

        foreach (var b in _bits) DrawBit(dc, b, Compact ? 1 : Math.Max(1, p * 0.6));
    }

    private static readonly Dictionary<string, string[]> Shapes2 = new()
    {
        ["note"] = new[] { "..XX", "..XX", "..X.", "..X.", "XXX.", "XX.." },
        ["heart"] = new[] { ".XX.XX.", "XXXXXXX", "XXXXXXX", ".XXXXX.", "..XXX..", "...X..." },
        ["spark"] = new[] { "..X..", "..X..", "XXXXX", "..X..", "..X.." },
        ["dust"] = new[] { ".X.", "XXX", ".X." },
        ["z"] = new[] { "XXXX", "..X.", ".X..", "XXXX" },
        ["confetti"] = new[] { "X", "X" },
        ["sweat"] = new[] { ".X.", "XXX", "XXX", ".X." },
    };

    private void DrawBit(DrawingContext dc, Bit b, double q)
    {
        var map = Shapes2[b.Kind];
        double life = b.Kind == "spark" ? Math.Sin(b.Life * Math.PI) : Math.Min(1, b.Life * 1.6);
        var brush = Frozen(b.Color, life);
        for (int r = 0; r < map.Length; r++)
            for (int c = 0; c < map[r].Length; c++)
                if (map[r][c] == 'X') dc.DrawRectangle(brush, null, new Rect(Math.Round(b.X + c * q), Math.Round(b.Y + r * q), q, q));
    }

    private static readonly Dictionary<(Color, double), Brush> BrushCache = new();

    private static Brush Frozen(Color c, double opacity = 1)
    {
        opacity = Math.Round(Math.Clamp(opacity, 0, 1), 2);
        if (BrushCache.TryGetValue((c, opacity), out var b)) return b;
        if (BrushCache.Count > 400) BrushCache.Clear();
        b = new SolidColorBrush(Color.FromArgb((byte)(c.A * opacity), c.R, c.G, c.B));
        b.Freeze();
        return BrushCache[(c, opacity)] = b;
    }

    // ---------- the sprite ----------

    private readonly record struct Pose(bool Step, bool Closed, bool ArmsUp, bool Open, int Wave, bool Sit);

    private static readonly Dictionary<string, BitmapSource> SpriteCache = new();

    /// <summary>The picture for this look and pose; built once and kept, so drawing a frame is just one image.</summary>
    public static BitmapSource Sprite(Settings s, object poseObj = null)
    {
        var pose = poseObj is Pose ps ? ps : default;
        string key = $"{s.PetShape}|{s.PetEars}|{s.PetEyes}|{s.PetMouth}|{s.PetPattern}|{s.PetHat}|{s.PetColor}|{s.PetColor2}|{s.PetEyeColor}|{s.PetOutline}|{s.PetCheeks}|{s.PetAccessory}|{s.PetShading}|{pose}";
        if (SpriteCache.TryGetValue(key, out var cached)) return cached;
        if (SpriteCache.Count > 300) SpriteCache.Clear();
        var grid = Build(s, pose);
        var px = new byte[W * H * 4];
        var body = Settings.ToColor(s.PetColor, 0xFF, Colors.White);
        var second = Settings.ToColor(s.PetColor2, 0xFF, Colors.Orange);
        var eye = Settings.ToColor(s.PetEyeColor, 0xFF, Colors.Black);
        Color Of(char ch) => ch switch
        {
            'B' => body, 'L' => Mix(body, Colors.White, 0.35), 'l' => Mix(body, Colors.White, 0.65), 'S' => Mix(body, Colors.Black, 0.28), 's' => Mix(body, Colors.Black, 0.45),
            'A' => second, 'a' => Mix(second, Colors.Black, 0.3), 'O' => Mix(body, Color.FromRgb(0x08, 0x08, 0x0C), 0.82),
            'E' => eye, 'W' => Colors.White, 'K' => Color.FromArgb(0xC0, 0xFF, 0x8F, 0xA3), 'P' => Color.FromRgb(0xFF, 0x6B, 0x8A),
            'V' => second, 'v' => Mix(second, Colors.White, 0.5), 'G' => Color.FromRgb(0xFF, 0xC9, 0x40), 'g' => Color.FromRgb(0xC9, 0x8A, 0x1E),
            'R' => Color.FromRgb(0xFF, 0x45, 0x3A), 'J' => Color.FromRgb(0x4F, 0xD1, 0xC5), 'H' => second, 'h' => Mix(second, Colors.White, 0.4),
            'D' => Color.FromRgb(0x2E, 0x2E, 0x36), 'd' => Color.FromRgb(0x55, 0x55, 0x60), 'U' => Color.FromRgb(0x8E, 0x5C, 0xFF), 'u' => Color.FromRgb(0x5E, 0x38, 0xC0),
            'Y' => Color.FromRgb(0xFF, 0xD6, 0x0A), 'F' => Colors.White, 'N' => Color.FromRgb(0xFF, 0x8A, 0x2A), 'n' => Color.FromRgb(0xD9, 0x66, 0x1A),
            _ => Colors.Transparent,
        };
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                char ch = grid[y, x];
                if (ch == '\0') continue;
                var c = Of(ch);
                int i = (y * W + x) * 4;
                px[i] = (byte)(c.B * c.A / 255); px[i + 1] = (byte)(c.G * c.A / 255); px[i + 2] = (byte)(c.R * c.A / 255); px[i + 3] = c.A;
            }
        var image = BitmapSource.Create(W, H, 96, 96, PixelFormats.Pbgra32, null, px, W * 4);
        image.Freeze();
        return SpriteCache[key] = image;
    }

    private static readonly Dictionary<string, int> TopCache = new();

    /// <summary>First row of the standing sprite that holds a pixel (ears, hat). Rows above it are empty.</summary>
    public static int ContentTop(Settings s)
    {
        string key = $"{s.PetShape}|{s.PetEars}|{s.PetHat}|{s.PetOutline}";
        if (TopCache.TryGetValue(key, out var top)) return top;
        var grid = Build(s, default);
        top = 0;
        while (top < H - 1 && Enumerable.Range(0, W).All(x => grid[top, x] == '\0')) top++;
        // Arms up while dancing reach a little higher; keep one row for them.
        return TopCache[key] = Math.Max(0, top - 1);
    }

    private static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static double Sq(double v) => v * v;

    private static char[,] Build(Settings s, Pose pose)
    {
        var g = new char[H, W];
        int shape = Math.Clamp(s.PetShape, 0, Shapes.Length - 1);
        void Set(int x, int y, char c) { y += OY; if (x >= 0 && x < W && y >= 0 && y < H) g[y, x] = c; }
        char Get(int x, int y) { y += OY; return x >= 0 && x < W && y >= 0 && y < H ? g[y, x] : '\0'; }
        void Ellipse(double cx, double cy, double rx, double ry, char c, bool onlyFilled = false)
        {
            for (int y = (int)(cy - ry - 1); y <= cy + ry + 1; y++)
                for (int x = (int)(cx - rx - 1); x <= cx + rx + 1; x++)
                    if (Sq((x + 0.5 - cx) / rx) + Sq((y + 0.5 - cy) / ry) <= 1 && (!onlyFilled || Get(x, y) != '\0')) Set(x, y, c);
        }
        void Rect(int x0, int y0, int x1, int y1, char c) { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y, c); }
        void Mirror(int x, int y, char c) { Set(x, y, c); Set(W - 1 - x, y, c); }

        // Body geometry per shape: centre, radii, top of head, eye line, bottom.
        var (cx, cy, rx, ry, top, eyeY, bottom) = shape switch
        {
            1 => (16.0, 19.5, 11.0, 10.5, 9, 17, 30),
            2 => (16.0, 18.0, 9.0, 12.0, 6, 15, 30),
            3 => (16.0, 17.0, 11.0, 11.0, 5, 15, 29),
            4 => (16.0, 21.0, 12.0, 9.0, 12, 19, 30),
            _ => (16.0, 19.0, 11.0, 10.0, 9, 17, 29),
        };

        // Ears, behind the body.
        switch (s.PetEars)
        {
            case 1: case 6:
                int tall = s.PetEars == 6 ? 2 : 0;
                for (int y = 0; y <= 8 + tall; y++)
                {
                    int half = (int)Math.Round(y * 0.55);
                    for (int x = -half; x <= half; x++) { Mirror(9 + x, top - 6 - tall + y, 'B'); if (Math.Abs(x) < half - 1 && y > 3) Mirror(9 + x, top - 6 - tall + y, 'A'); }
                }
                break;
            case 2:
                for (int side = -1; side <= 1; side += 2) { Ellipse(16 + side * 5, top - 4, 2.6, 7, 'B'); Ellipse(16 + side * 5, top - 3, 1.2, 5, 'A'); }
                break;
            case 3:
                for (int side = -1; side <= 1; side += 2) { Ellipse(16 + side * 8.5, top + 1, 3.6, 3.6, 'B'); Ellipse(16 + side * 8.5, top + 1, 1.8, 1.8, 'A'); }
                break;
            case 4:
                for (int y = top - 6; y <= top; y++) Set(15, y, 'D');
                Ellipse(15.5, top - 7, 2.2, 2.2, 'A');
                Set(15, top - 8, 'l');
                break;
            case 5:
                for (int i = 0; i < 6; i++) { Mirror(9 - i / 2, top + 1 - i, i < 3 ? 'd' : 'L'); Mirror(10 - i / 2, top + 1 - i, i < 3 ? 'd' : 'L'); }
                break;
        }

        // Body.
        for (int y = top - 1; y <= bottom + 1; y++)
            for (int x = 0; x < W; x++)
            {
                double px = x + 0.5, py = y + 0.5;
                bool inside = shape switch
                {
                    1 => px >= cx - rx && px <= cx + rx && py >= top && py <= bottom && !(Corner(px, py, cx - rx + 4, top + 4) || Corner(px, py, cx + rx - 4, top + 4) || Corner(px, py, cx - rx + 4, bottom - 4) || Corner(px, py, cx + rx - 4, bottom - 4)),
                    3 => py <= 16 ? Sq((px - cx) / rx) + Sq((py - 16.5) / 11.5) <= 1 : px >= cx - rx && px <= cx + rx && py <= bottom - ((int)((px - 5) / 4) + pose.Wave) % 2 * 2,
                    _ => Sq((px - cx) / rx) + Sq((py - cy) / ry) <= 1,
                };
                if (inside) Set(x, y, 'B');
            }
        bool Corner(double px, double py, double ccx, double ccy) =>
            Math.Abs(px - ccx) > 0 && Math.Abs(py - ccy) > 0 && ((px < ccx && ccx < cx) || (px > ccx && ccx > cx)) && ((py < ccy && ccy < cy) || (py > ccy && ccy > cy)) && Sq(px - ccx) + Sq(py - ccy) > 16;

        // Feet (walking lifts one) and little arms (up while dancing).
        if (shape != 3 && !pose.Sit)
        {
            char foot = s.PetMouth == 6 ? 'N' : 'B'; // a beak comes with orange feet, like a penguin
            Ellipse(11, bottom + 1 - (pose.Step ? 1 : 0), 3, 1.7, foot);
            Ellipse(21, bottom + 1 - (pose.Step ? 0 : 1), 3, 1.7, foot);
        }
        double armY = pose.ArmsUp ? cy - 4 : cy + 2;
        Ellipse(cx - rx - 0.5, armY, 2, 1.6, 'B');
        Ellipse(cx + rx + 0.5, pose.Open && !pose.ArmsUp ? cy - 4 : cy + 2, 2, 1.6, 'B');

        // Light from the top left: highlights, shades and a darker rim at the bottom.
        var shaded = (char[,])g.Clone();
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                if (g[y, x] != 'B') continue;
                double nx = (x + 0.5 - cx) / rx, ny = (y - OY + 0.5 - cy) / ry;
                double light = -0.3 * nx - 0.9 * ny; // light mostly from above: soft bands instead of a hard diagonal
                bool edgeBelow = y + 1 >= H || g[y + 1, x] == '\0';
                // Shading strength: flat (none), soft (one light and one shade step) or strong (two of each).
                char c = s.PetShading switch
                {
                    0 => 'B',
                    1 => light > 0.6 ? 'L' : light < -0.55 ? 'S' : 'B',
                    _ => light > 0.95 ? 'l' : light > 0.45 ? 'L' : light < -0.75 ? 's' : light < -0.35 ? 'S' : 'B',
                };
                if (edgeBelow && s.PetShading > 0) c = 'S';
                shaded[y, x] = c;
            }
        g = shaded;

        // Pattern in the second color, keeping the shading.
        bool Body(int x, int y) => Get(x, y) is 'B' or 'L' or 'l' or 'S' or 's';
        void Paint(int x, int y) { if (Body(x, y)) Set(x, y, Get(x, y) is 'S' or 's' ? 'a' : 'A'); }
        switch (s.PetPattern)
        {
            case 1: for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) if (Sq((x + 0.5 - cx) / (rx * 0.45)) + Sq((y + 0.5 - (cy + ry * 0.68)) / (ry * 0.3)) <= 1) Paint(x, y); break;
            case 2: for (int y = top; y <= bottom; y += 3) for (int x = 0; x < W; x++) if (Math.Abs(x + 0.5 - cx) > rx * 0.45) { Paint(x, y); } break;
            case 3: foreach (var (sx, sy, r) in new[] { (9.0, cy + 3, 2.4), (22.0, cy - 4, 2.0), (20.0, cy + 6, 1.6), (12.0, top + 3.0, 1.4) }) for (int y = (int)(sy - r); y <= sy + r; y++) for (int x = (int)(sx - r); x <= sx + r; x++) if (Sq(x + 0.5 - sx) + Sq(y + 0.5 - sy) <= r * r) Paint(x, y); break;
            case 4: for (int y = (int)(cy + 2); y <= bottom + 2; y++) for (int x = 0; x < W; x++) Paint(x, y); break;
            case 5: foreach (var (sx, sy) in new[] { (9, (int)cy + 3), (22, (int)cy - 2), (13, top + 4), (20, (int)cy + 6) }) { Paint(sx, sy); Paint(sx - 1, sy); Paint(sx + 1, sy); Paint(sx, sy - 1); Paint(sx, sy + 1); } break;
            case 6: // penguin mask: a heart-shaped face and a big belly in the second color
                for (int y = top - 2; y <= bottom + 2; y++)
                    for (int x = 0; x < W; x++)
                    {
                        double px = x + 0.5, py = y + 0.5;
                        bool face = Sq(px - (cx - 4)) + Sq(py - eyeY) <= 4.8 * 4.8 || Sq(px - (cx + 4)) + Sq(py - eyeY) <= 4.8 * 4.8;
                        bool belly = Sq((px - cx) / (rx * 0.66)) + Sq((py - (cy + ry * 0.38)) / (ry * 0.58)) <= 1;
                        if ((face && py > eyeY - 4) || belly) Paint(x, y);
                    }
                break;
        }

        // Face.
        int e1 = shape == 2 ? 12 : 11, e2 = shape == 2 ? 20 : 21, ey = eyeY;
        void Eye(int ex)
        {
            if (pose.Closed && s.PetEyes != 4)
            {
                Rect(ex - 2, ey + 1, ex + 1, ey + 1, 'E');
                Set(ex - 2, ey, 'E'); Set(ex + 1, ey, 'E');
                return;
            }
            switch (s.PetEyes)
            {
                case 0: Rect(ex - 1, ey, ex, ey + 1, 'E'); Set(ex - 1, ey, 'W'); break;
                case 2: Rect(ex - 2, ey, ex + 1, ey, 'S'); Rect(ex - 2, ey + 1, ex + 1, ey + 1, 'E'); Rect(ex - 1, ey + 2, ex, ey + 2, 'E'); break;
                case 3: Set(ex - 2, ey + 1, 'E'); Set(ex - 1, ey, 'E'); Set(ex, ey, 'E'); Set(ex + 1, ey + 1, 'E'); break;
                case 5: for (int i = -2; i <= 2; i++) { Set(ex + i, ey, 'Y'); Set(ex, ey + i, 'Y'); } Set(ex - 1, ey - 1, 'Y'); Set(ex + 1, ey + 1, 'Y'); Set(ex, ey, 'W'); break;
                default:
                    Rect(ex - 2, ey - 2, ex + 1, ey + 2, 'E');
                    Rect(ex - 2, ey - 2, ex - 1, ey - 1, 'W');
                    Set(ex + 1, ey + 2, 'W');
                    break;
            }
        }
        if (s.PetEyes == 4)
        {
            Rect(7, ey - 2, 25, ey + 1, 'V');
            Rect(8, ey - 2, 24, ey - 2, 'v');
            if (pose.Closed) Rect(8, ey, 24, ey, 'a'); else { Set(10, ey - 1, 'W'); Set(11, ey - 1, 'W'); Set(10, ey, 'W'); }
        }
        else { Eye(e1); Eye(e2); }

        int my = ey + (s.PetEyes == 4 ? 4 : 5);
        int mouth = pose.Open && s.PetMouth != 0 ? 4 : s.PetMouth;
        switch (mouth)
        {
            case 1: case 3: case 5:
                Set(14, my, 'E'); Rect(15, my + 1, 17, my + 1, 'E'); Set(18, my, 'E');
                if (mouth == 3) { Set(15, my + 2, 'W'); Set(17, my + 2, 'W'); }
                if (mouth == 5) { Set(16, my + 2, 'P'); Set(17, my + 2, 'P'); Set(16, my + 3, 'P'); }
                break;
            case 2: Set(14, my, 'E'); Set(15, my + 1, 'E'); Set(16, my, 'E'); Set(17, my + 1, 'E'); Set(18, my, 'E'); break;
            case 4: Rect(15, my, 17, my + 2, 'E'); Set(16, my + 1, 'P'); break;
            case 6: Rect(13, ey + 2, 18, ey + 2, 'N'); Rect(14, ey + 3, 17, ey + 3, 'n'); break;
        }
        if (s.PetCheeks) { Rect(e1 - 4, my - 1, e1 - 2, my - 1, 'K'); Rect(e2 + 2, my - 1, e2 + 4, my - 1, 'K'); }

        // Accessories around the face and neck.
        int neck = my + 3;
        switch (s.PetAccessory)
        {
            case 1: // glasses: two frames and a bridge
                foreach (int ex in new[] { e1, e2 })
                {
                    Rect(ex - 3, ey - 3, ex + 2, ey - 3, 'D'); Rect(ex - 3, ey + 3, ex + 2, ey + 3, 'D');
                    Rect(ex - 3, ey - 3, ex - 3, ey + 3, 'D'); Rect(ex + 2, ey - 3, ex + 2, ey + 3, 'D');
                }
                Rect(e1 + 3, ey - 1, e2 - 4, ey - 1, 'D');
                break;
            case 2: // sunglasses
                foreach (int ex in new[] { e1, e2 }) { Rect(ex - 3, ey - 2, ex + 2, ey + 2, 'D'); Set(ex - 2, ey - 1, 'd'); Set(ex - 1, ey - 1, 'd'); }
                Rect(e1 + 3, ey - 1, e2 - 4, ey - 1, 'D');
                break;
            case 3: // scarf with a hanging end
                for (int x = 0; x < W; x++) for (int y = neck; y <= neck + 1; y++) if (Body(x, y)) Set(x, y, (x + y) % 4 == 0 ? 'F' : 'R');
                Rect(20, neck + 2, 22, neck + 6, 'R'); Rect(20, neck + 4, 22, neck + 4, 'F');
                break;
            case 4: // bow tie
                foreach (var (dx, dy) in new[] { (0, 0), (-1, -1), (-1, 0), (-1, 1), (-2, -1), (-2, 0), (-2, 1), (-3, -2), (-3, -1), (-3, 0), (-3, 1), (-3, 2), (1, -1), (1, 0), (1, 1), (2, -1), (2, 0), (2, 1), (3, -2), (3, -1), (3, 0), (3, 1), (3, 2) })
                    Set(16 + dx, neck + 1 + dy, dx == 0 ? 'D' : 'R');
                break;
            case 5: // collar with a bell
                for (int x = 0; x < W; x++) if (Body(x, neck)) Set(x, neck, 'R');
                Rect(15, neck + 1, 17, neck + 2, 'G'); Set(15, neck + 1, 'Y'); Set(16, neck + 3, 'g');
                break;
        }

        // Hat on top.
        switch (s.PetHat)
        {
            case 1: // cap with brim
                for (int r = 0; r < 4; r++) { int half = 4 + r * 2; Rect(16 - half, top - 4 + r, 15 + half - (r == 3 ? 0 : 0), top - 4 + r, 'H'); }
                Rect(24, top - 1, 29, top - 1, 'H'); Rect(12, top - 3, 14, top - 3, 'h'); Set(16, top - 5, 'h');
                break;
            case 2: // crown with jewels
                Rect(9, top - 3, 22, top - 1, 'G'); Rect(9, top - 1, 22, top - 1, 'g');
                foreach (var (x, h) in new[] { (9, 2), (12, 3), (15, 4), (16, 4), (19, 3), (22, 2) }) for (int i = 1; i <= h; i++) Set(x, top - 3 - i, 'G');
                Set(12, top - 2, 'R'); Set(19, top - 2, 'R'); Set(15, top - 2, 'J'); Set(16, top - 2, 'J'); Set(15, top - 7, 'W');
                break;
            case 3: // headphones
                for (int x = 0; x < W; x++)
                    for (int y = top - 5; y < ey - 2; y++)
                    {
                        double d = Math.Sqrt(Sq((x + 0.5 - 16) / 13.5) + Sq((y + 0.5 - (ey + 1)) / (ey + 1 - top + 5)));
                        if (d > 0.93 && d <= 1.02) Set(x, y, 'D');
                    }
                Rect(1, ey - 3, 4, ey + 3, 'D'); Rect(27, ey - 3, 30, ey + 3, 'D'); Rect(2, ey - 2, 3, ey + 2, 'H'); Rect(28, ey - 2, 29, ey + 2, 'H');
                break;
            case 4: // beanie with pom-pom
                for (int r = 0; r < 6; r++) { int half = 5 + Math.Min(r, 3) * 2; Rect(16 - half, top - 5 + r, 15 + half, top - 5 + r, r >= 4 ? 'h' : r % 2 == 0 ? 'H' : 'F'); }
                Ellipse(16, top - 7, 2.3, 2.3, 'F');
                break;
            case 5: // halo
                for (int x = 0; x < W; x++) for (int y = top - 8; y < top - 2; y++) { double d = Sq((x + 0.5 - 16) / 7.5) + Sq((y + 0.5 - (top - 5)) / 1.9); if (d <= 1 && d > 0.35) Set(x, y, 'G'); }
                break;
            case 6: // flower
                foreach (var (dx, dy) in new[] { (0, -2), (0, 2), (-2, 0), (2, 0), (-1, -1), (1, -1), (-1, 1), (1, 1) }) Set(23 + dx, top - 1 + dy, 'P');
                Set(23, top - 1, 'Y'); Set(21, top + 2, 'L');
                break;
            case 7: // bow
                foreach (var (dx, dy) in new[] { (0, 0), (-1, -1), (-2, -1), (-3, -2), (-3, -1), (-3, 0), (-2, 0), (-1, 0), (1, -1), (2, -1), (3, -2), (3, -1), (3, 0), (2, 0), (1, 0), (-2, 1), (2, 1) }) Set(22 + dx, top + dy, dx == 0 ? 'R' : 'P');
                break;
            case 8: // wizard hat with a star
                for (int r = 0; r <= 10; r++) { int half = r / 2 + 1; Rect(18 - half - r / 4, top - 11 + r, 18 + half - r / 4, top - 11 + r, r % 4 == 3 ? 'u' : 'U'); }
                Rect(6, top - 1, 25, top - 1, 'u'); Set(16, top - 5, 'Y'); Set(15, top - 5, 'Y'); Set(17, top - 5, 'Y'); Set(16, top - 6, 'Y'); Set(16, top - 4, 'Y');
                break;
        }

        // Dark outline around everything, the classic pixel art edge.
        if (s.PetOutline)
        {
            var outlined = (char[,])g.Clone();
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    if (g[y, x] != '\0') continue;
                    bool near = (x > 0 && g[y, x - 1] != '\0') || (x < W - 1 && g[y, x + 1] != '\0') || (y > 0 && g[y - 1, x] != '\0') || (y < H - 1 && g[y + 1, x] != '\0');
                    if (near) outlined[y, x] = 'O';
                }
            g = outlined;
        }
        return g;
    }
}
