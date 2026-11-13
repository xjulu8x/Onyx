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

    private double Fps => Compact ? 12 : 24; // smooth motion; resting frames are skipped below

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
        double k = 11.0 / Fps; // all motion below is per 1/11 s, so it looks the same at any frame rate
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
            _vel *= Math.Pow(0.6, k);
            if (_hop <= 0 && level > 0.4) { _hopSpeed = 2.5 + level * 4; _armBeat++; }
            if (On(0) && _frame % Frames(0.64) == 0) Add("note", _x + _random.Next(4, 28) * p, head, (_random.NextDouble() - 0.5) * 0.6, -0.9, Settings.ToColor(s.PetColor2, 0xFF, Colors.White));
            if (_frame % Frames(2.7) == 0) _facing = -_facing;
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
                    _vel += (want - _vel) * (1 - Math.Pow(0.7, k));
                    if (Math.Abs(d) < 0.6 && Math.Abs(_vel) < 0.2) { _vel = 0; Start(Act.Idle, Frames(1 + _random.NextDouble() * 2)); }
                    if (s.PetBehavior != 0) Start(Act.Idle, 1);
                    break;
                case Act.Look:
                    if (_left % Frames(0.9) == 0) _facing = -_facing; // looks left and right
                    _vel *= Math.Pow(0.5, k);
                    break;
                default:
                    _vel *= Math.Pow(0.5, k);
                    break;
            }
        }
        if (Math.Abs(_vel) > 0.05)
        {
            _x += _vel * k;
            _stride += Math.Abs(_vel) * k;
            _facing = Math.Sign(_vel);
            if (On(3) && !Compact && _hop <= 0 && (int)(_stride / (6 * p)) != (int)((_stride - Math.Abs(_vel) * k) / (6 * p)))
                Add("dust", _x + (_facing > 0 ? 8 : W - 8) * p, ActualHeight - 3 * p, -_facing * 0.3, -0.2, Color.FromRgb(0x8A, 0x8A, 0x8A));
            if (On(6) && !Compact) _trail.Add((_x, 1));
        }
        if (_x < 0 || _x > floor) { _x = Math.Clamp(_x, 0, floor); _vel = 0; if (_act == Act.Walk) Start(Act.Look, Frames(1.5)); }
        _target = Math.Clamp(_target, 0, floor);

        // Looks at the mouse when it comes close, unless asleep or busy walking.
        if (_frame % Frames(0.27) == 0) _watching = false;
        if (_frame % Frames(0.27) == 0 && !Sleeping && _act != Act.Walk && IsVisible && Native.GetCursorPos(out var cursor))
        {
            var me = PointToScreen(new Point(_x + W * p / 2, ActualHeight - VisibleRows * p / 2));
            double dx = cursor.X - me.X, dy = cursor.Y - me.Y;
            if (Math.Abs(dx) < 260 && Math.Abs(dy) < 160 && Math.Abs(dx) > 6) { _facing = Math.Sign(dx); _watching = true; }
        }

        // Hop physics with squash on landing.
        if (_hopSpeed > 0 || _hop > 0)
        {
            _hop += _hopSpeed * k;
            _hopSpeed -= (Compact ? 0.7 : 1.4) * k;
            if (_hop <= 0) { _hop = 0; _hopSpeed = 0; _squash = 0.84; }
        }
        _squash += (1 - _squash) * (1 - Math.Pow(0.65, k));
        if (_hop > 0) _squash = 1.05;

        if (!Sleeping && --_blink < -Frames(2.5) - _random.Next(Frames(4))) _blink = 3; // half, closed, half: a soft blink
        _aura = music ? Math.Max(_aura * Math.Pow(0.85, k), level) : _aura * Math.Pow(0.9, k);

        // Ambient effects.
        if (Sleeping && On(4) && _frame % Frames(1.3) == 0) Add("z", _x + (_facing > 0 ? 22 : 8) * p, head, 0.25, -0.5, Color.FromRgb(0xC8, 0xC8, 0xD0));
        if (!Sleeping && On(2) && !Compact && _random.NextDouble() < 0.05 * k) Add("spark", _x + _random.Next(-4, W + 4) * p, head + _random.Next(0, VisibleRows) * p, 0, 0, Color.FromRgb(0xFF, 0xE0, 0x8A));
        if (On(8) && !Compact && _frame % Frames(0.8) == 0 && (SystemMonitor.Instance.CpuTemp > 80 || SystemMonitor.Instance.Cpu > 85))
            Add("sweat", _x + (_facing > 0 ? 25 : 7) * p, head + 4 * p, _facing * 0.2, 0, Color.FromRgb(0x7F, 0xD0, 0xFF));

        foreach (var b in _bits)
        {
            b.X += b.Vx * k; b.Y += b.Vy * k;
            if (b.Kind is "confetti" or "sweat") b.Vy += 0.25 * k;
            if (b.Kind is "note" or "heart" or "z") b.X += Math.Sin(b.Life * 8) * 0.3 * k;
            b.Life -= (b.Kind switch { "spark" => 0.12, "dust" => 0.1, "confetti" => 0.035, _ => 0.045 }) * 11 / Fps;
        }
        _bits.RemoveAll(b => b.Life <= 0 || b.Y > ActualHeight + 4 || b.Y < -20);
        for (int i = _trail.Count - 1; i >= 0; i--) { var t = _trail[i]; t.Life -= 0.08 * k; if (t.Life <= 0) _trail.RemoveAt(i); else _trail[i] = t; }

        // Redraw only when the picture changes, so resting costs next to nothing (most of all in the notch).
        Secondary(k, music && !Compact);
        _pose = CurrentPose(music && !Compact);
        var look = (Math.Round(_x * 2), Math.Round(_hop * 2), _bits.Count, _facing, Math.Round(_squash, 2), _trail.Count, _pose, Sleeping ? _frame / Frames(1) : 0);
        double moved = Math.Round(_earRot) + Math.Round(_armL) * 64 + Math.Round(_turn * 8) * 4096 + Math.Round(_hatLag) * 65536 + Math.Round(_footL * 3 + _footR * 7) * 1048576;
        if (Compact && look == _lastLook && moved == _lastMoved && _bits.Count == 0) return; // the big one keeps breathing
        _lastMoved = moved;
        _lastLook = look;
        InvalidateVisual();
    }

    private Pose _pose;
    private double _lastMoved;
    private double _sink;
    private (double, double, int, int, double, int, Pose, int) _lastLook;

    private Pose CurrentPose(bool dancing)
    {
        double p = Pixel;
        bool walking = Math.Abs(_vel) > 0.05;
        bool yawning = _act == Act.Yawn && _left > _actLength * 0.3;
        _ = p;
        return new Pose(
            Closed: Sleeping || _blink == 2 || yawning,
            Half: _blink is 1 or 3,
            Open: dancing || yawning,
            Wave: _frame / Frames(0.4) % 2,
            Sit: (_act == Act.Sit || Sleeping) && !walking && !dancing);
    }

    // ---------- secondary motion: parts that follow the body on springs ----------

    private double _earRot, _earVel, _hatLag, _hatRot, _hatVel, _armL, _armR, _footL, _footR, _turn = 1, _bodyY, _bodyVy;

    /// <summary>
    /// Ears and hat lag behind the body and swing back, arms swing with the steps or the music, feet lift in turn,
    /// and turning around squeezes the character through its middle instead of flipping it. All per time, not per frame.
    /// </summary>
    private void Secondary(double k, bool dancing)
    {
        double p = Pixel, t = _frame / Fps;
        bool walking = Math.Abs(_vel) > 0.05;
        double phase = _stride / (3.5 * p) * Math.PI;
        _sink += ((_pose.Sit ? 2 * p : 0) - _sink) * (1 - Math.Pow(0.65, k));

        // Vertical speed of the body (hops and steps) drives the ears and the hat.
        double bob = walking ? Math.Abs(Math.Sin(phase)) * p * 0.6 : 0;
        double bodyY = _hop + bob - _sink;
        double vy = (bodyY - _bodyY) / Math.Max(0.01, k);
        _bodyVy += (vy - _bodyVy) * 0.5;
        _bodyY = bodyY;

        double Spring(ref double value, ref double velocity, double target, double stiffness, double damping)
        {
            velocity += (target - value) * stiffness * k;
            velocity *= Math.Pow(damping, k);
            value += velocity * k;
            return value;
        }

        double sway = Sleeping || Compact ? 0 : Math.Sin(t * 1.3) * 2.5; // in the notch it rests still, so it can skip frames
        Spring(ref _earRot, ref _earVel, Math.Clamp(-_vel * 7 / Math.Max(1, p / 2) + _bodyVy * 1.6 + sway, -22, 22), 0.28, 0.62);
        Spring(ref _hatRot, ref _hatVel, Math.Clamp(-_vel * 5 / Math.Max(1, p / 2), -14, 14), 0.3, 0.6);
        _hatLag += (Math.Clamp(-_bodyVy * 0.35, -2.5 * p, 2.5 * p) - _hatLag) * (1 - Math.Pow(0.6, k));

        double armL, armR;
        bool yawning = _act == Act.Yawn && _left > _actLength * 0.3;
        if (dancing) { armL = -70 + Math.Sin(t * 7.5) * 35; armR = -70 + Math.Sin(t * 7.5 + Math.PI) * 35; }
        else if (yawning) armL = armR = -115;
        else if (walking) { armL = Math.Sin(phase) * 24; armR = -Math.Sin(phase) * 24; }
        else if (Sleeping) armL = armR = 8;
        else if (Compact) armL = armR = 0;
        else { armL = Math.Sin(t * 1.6) * 4; armR = Math.Sin(t * 1.6 + 1) * 4; }
        _armL += (armL - _armL) * (1 - Math.Pow(0.55, k));
        _armR += (armR - _armR) * (1 - Math.Pow(0.55, k));

        double liftL = walking ? Math.Max(0, Math.Sin(phase)) * 1.1 : 0, liftR = walking ? Math.Max(0, -Math.Sin(phase)) * 1.1 : 0;
        _footL += (liftL - _footL) * (1 - Math.Pow(0.4, k));
        _footR += (liftR - _footR) * (1 - Math.Pow(0.4, k));

        _turn += (_facing - _turn) * (1 - Math.Pow(0.5, k));
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

        // Sitting down and getting up glide instead of jumping; walking bobs with each step; resting breathes.
        double t = _frame / Fps;
        double bob = Math.Abs(_vel) > 0.05 ? Math.Abs(Math.Sin(_stride / (3.5 * p) * Math.PI)) * p * 0.6 : 0;
        double cx = _x + W * p / 2, bottom = ActualHeight - (Compact ? 0 : 1) - _hop + _sink - bob;
        double breathe = 1 + Math.Sin(t * (Sleeping ? 1.6 : 2.4)) * (Sleeping ? 0.03 : 0.012);
        // Turning: the character narrows through its middle and opens up the other way.
        double turn = Math.Sign(_turn == 0 ? _facing : _turn) * Math.Max(0.12, Math.Abs(_turn));
        dc.PushTransform(new TranslateTransform(cx, bottom));
        dc.PushTransform(new ScaleTransform(turn * (2 - _squash), _squash * breathe));

        var (gcx, gcy, grx, _, gtop, _, gbottom) = Geometry(Math.Clamp(s.PetShape, 0, Shapes.Length - 1));
        var rect = new Rect(-W * p / 2, -H * p, W * p, H * p);
        Point At(double gx, double gy) => new((gx - W / 2.0) * p, (gy + OY - H) * p); // grid point in the drawing space
        void DrawPart(Part part, Transform move)
        {
            if (move != null) dc.PushTransform(move);
            dc.DrawImage(Sprite(s, _pose, part), rect);
            if (move != null) dc.Pop();
        }
        Transform Rotate(double angle, Point pivot) => new RotateTransform(angle, pivot.X, pivot.Y);

        DrawPart(PixelPet.Part.Ears, Rotate(_earRot, At(16, gtop + 2)));
        if (!_pose.Sit)
        {
            DrawPart(PixelPet.Part.FootL, new TranslateTransform(0, -_footL * p));
            DrawPart(PixelPet.Part.FootR, new TranslateTransform(0, -_footR * p));
        }
        DrawPart(PixelPet.Part.Body, null);
        DrawPart(PixelPet.Part.ArmL, Rotate(-_armL, At(gcx - grx + 1, gcy + 1.5)));
        DrawPart(PixelPet.Part.ArmR, Rotate(_armR, At(gcx + grx - 1, gcy + 1.5)));
        var hatMove = new TransformGroup();
        hatMove.Children.Add(Rotate(_hatRot, At(16, gtop + 1)));
        hatMove.Children.Add(new TranslateTransform(0, _hatLag));
        DrawPart(PixelPet.Part.Hat, hatMove);
        _ = gbottom;
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

    private readonly record struct Pose(bool Closed, bool Half, bool Open, int Wave, bool Sit);

    /// <summary>Fine pixels per grid pixel: shapes, outline and shading are drawn at twice the grid, so curves stay round and detailed.</summary>
    private const int S = 2;

    private static readonly Dictionary<string, BitmapSource> SpriteCache = new();

    /// <summary>The picture for this look and pose; built once and kept, so drawing a frame is just one image.</summary>
    public static BitmapSource Sprite(Settings s, object poseObj = null, Part part = Part.Body)
    {
        var pose = poseObj is Pose ps ? ps : default;
        if (part != Part.Body) pose = pose with { Closed = false, Half = false, Open = false, Wave = 0 }; // only the body changes with the pose
        string key = $"{s.PetShape}|{s.PetEars}|{s.PetEyes}|{s.PetMouth}|{s.PetPattern}|{s.PetHat}|{s.PetColor}|{s.PetColor2}|{s.PetEyeColor}|{s.PetOutline}|{s.PetCheeks}|{s.PetAccessory}|{s.PetShading}|{pose}|{part}";
        if (SpriteCache.TryGetValue(key, out var cached)) return cached;
        if (SpriteCache.Count > 400) SpriteCache.Clear();
        return SpriteCache[key] = ToImage(s, Build(s, pose, part));
    }

    /// <summary>All parts in one picture, for the small tiles in the settings.</summary>
    public static BitmapSource Composite(Settings s)
    {
        string key = "all|" + string.Join("|", typeof(Settings).GetProperties().Where(p => p.Name.StartsWith("Pet")).Select(p => p.GetValue(s)));
        if (SpriteCache.TryGetValue(key, out var cached)) return cached;
        var all = new char[H * S, W * S];
        foreach (var part in new[] { Part.Ears, Part.FootL, Part.FootR, Part.Body, Part.ArmL, Part.ArmR, Part.Hat })
        {
            var g = Build(s, default, part);
            for (int y = 0; y < H * S; y++) for (int x = 0; x < W * S; x++) if (g[y, x] != '\0' && (all[y, x] == '\0' || g[y, x] != 'O' && g[y, x] != 'o')) all[y, x] = g[y, x];
        }
        return SpriteCache[key] = ToImage(s, all);
    }

    private static BitmapSource ToImage(Settings s, char[,] grid)
    {
        int fw = W * S, fh = H * S;
        var px = new byte[fw * fh * 4];
        var body = Settings.ToColor(s.PetColor, 0xFF, Colors.White);
        var second = Settings.ToColor(s.PetColor2, 0xFF, Colors.Orange);
        var eye = Settings.ToColor(s.PetEyeColor, 0xFF, Colors.Black);
        // Shadows lean a little toward blue and lights toward warm, like painted pixel art, instead of plain grey.
        var cool = Color.FromRgb(0x10, 0x14, 0x2A);
        var warm = Color.FromRgb(0xFF, 0xF6, 0xE0);
        Color Of(char ch) => ch switch
        {
            'B' => body, 'L' => Mix(body, warm, 0.3), 'l' => Mix(body, warm, 0.6), 'S' => Mix(body, cool, 0.26), 's' => Mix(body, cool, 0.46),
            'A' => second, 'a' => Mix(second, cool, 0.28), 'b' => Mix(second, warm, 0.35),
            'O' => Mix(body, Color.FromRgb(0x08, 0x08, 0x10), 0.84), 'o' => Mix(body, Color.FromRgb(0x08, 0x08, 0x10), 0.6),
            'E' => eye, 'e' => Mix(eye, Colors.White, 0.25), 'W' => Colors.White, 'K' => Color.FromArgb(0xB0, 0xFF, 0x8F, 0xA3), 'P' => Color.FromRgb(0xFF, 0x6B, 0x8A),
            'V' => second, 'v' => Mix(second, Colors.White, 0.5), 'G' => Color.FromRgb(0xFF, 0xC9, 0x40), 'g' => Color.FromRgb(0xC9, 0x8A, 0x1E),
            'R' => Color.FromRgb(0xFF, 0x45, 0x3A), 'r' => Color.FromRgb(0xB8, 0x24, 0x22), 'J' => Color.FromRgb(0x4F, 0xD1, 0xC5), 'H' => second, 'h' => Mix(second, Colors.White, 0.4),
            'D' => Color.FromRgb(0x2E, 0x2E, 0x36), 'd' => Color.FromRgb(0x5A, 0x5A, 0x66), 'U' => Color.FromRgb(0x8E, 0x5C, 0xFF), 'u' => Color.FromRgb(0x5E, 0x38, 0xC0),
            'Y' => Color.FromRgb(0xFF, 0xD6, 0x0A), 'F' => Colors.White, 'N' => Color.FromRgb(0xFF, 0x8A, 0x2A), 'n' => Color.FromRgb(0xD9, 0x66, 0x1A),
            _ => Colors.Transparent,
        };
        for (int y = 0; y < fh; y++)
            for (int x = 0; x < fw; x++)
            {
                char ch = grid[y, x];
                if (ch == '\0') continue;
                var c = Of(ch);
                int i = (y * fw + x) * 4;
                px[i] = (byte)(c.B * c.A / 255); px[i + 1] = (byte)(c.G * c.A / 255); px[i + 2] = (byte)(c.R * c.A / 255); px[i + 3] = c.A;
            }
        var image = BitmapSource.Create(fw, fh, 96, 96, PixelFormats.Pbgra32, null, px, fw * 4);
        image.Freeze();
        return image;
    }

    private static readonly Dictionary<string, int> TopCache = new();

    /// <summary>First grid row of the standing sprite that holds a pixel (ears, hat). Rows above it are empty.</summary>
    public static int ContentTop(Settings s)
    {
        string key = $"{s.PetShape}|{s.PetEars}|{s.PetHat}|{s.PetOutline}";
        if (TopCache.TryGetValue(key, out var top)) return top;
        top = H * S - 1;
        foreach (var part in new[] { Part.Body, Part.Ears, Part.Hat })
        {
            var grid = Build(s, default, part);
            int row = 0;
            while (row < top && Enumerable.Range(0, W * S).All(x => grid[row, x] == '\0')) row++;
            top = Math.Min(top, row);
        }
        // Arms up while dancing reach a little higher; keep one row for them.
        return TopCache[key] = Math.Max(0, top / S - 1);
    }

    private static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static double Sq(double v) => v * v;

    /// <summary>Body parts drawn on their own, so each can move with its own spring.</summary>
    public enum Part { Body, Ears, Hat, ArmL, ArmR, FootL, FootR }

    /// <summary>Centre, radii, top of head, eye line and bottom of a body shape, in grid units.</summary>
    private static (double Cx, double Cy, double Rx, double Ry, int Top, int EyeY, int Bottom) Geometry(int shape) => shape switch
    {
        1 => (16.0, 19.5, 11.0, 10.5, 9, 17, 30),
        2 => (16.0, 18.0, 9.0, 12.0, 6, 15, 30),
        3 => (16.0, 17.0, 11.0, 11.0, 5, 15, 29),
        4 => (16.0, 21.0, 12.0, 9.0, 12, 19, 30),
        _ => (16.0, 19.0, 11.0, 10.0, 9, 17, 29),
    };

    private static char[,] Build(Settings s, Pose pose, Part part = Part.Body)
    {
        int FW = W * S, FH = H * S;
        var g = new char[FH, FW];
        int shape = Math.Clamp(s.PetShape, 0, Shapes.Length - 1);
        // Fine pixels (fx, fy) and grid units: grid x = (fx + 0.5) / S, grid y = (fy + 0.5) / S - OY.
        void SetF(int fx, int fy, char c) { if (fx >= 0 && fx < FW && fy >= 0 && fy < FH) g[fy, fx] = c; }
        char GetF(int fx, int fy) => fx >= 0 && fx < FW && fy >= 0 && fy < FH ? g[fy, fx] : '\0';
        void Set(int x, int y, char c) { for (int dy = 0; dy < S; dy++) for (int dx = 0; dx < S; dx++) SetF(x * S + dx, (y + OY) * S + dy, c); }
        char Get(int x, int y) => GetF(x * S, (y + OY) * S);
        double GX(int fx) => (fx + 0.5) / S;
        double GY(int fy) => (fy + 0.5) / S - OY;
        void Fill(double x0, double y0, double x1, double y1, Func<double, double, bool> inside, char c)
        {
            for (int fy = Math.Max(0, (int)((y0 + OY) * S) - 1); fy <= Math.Min(FH - 1, (int)((y1 + OY) * S) + 1); fy++)
                for (int fx = Math.Max(0, (int)(x0 * S) - 1); fx <= Math.Min(FW - 1, (int)(x1 * S) + 1); fx++)
                    if (inside(GX(fx), GY(fy))) SetF(fx, fy, c);
        }
        void Ellipse(double ex, double ey2, double rx2, double ry2, char c) =>
            Fill(ex - rx2, ey2 - ry2, ex + rx2, ey2 + ry2, (x, y) => Sq((x - ex) / rx2) + Sq((y - ey2) / ry2) <= 1, c);
        void Rect(int x0, int y0, int x1, int y1, char c) { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y, c); }
        void Mirror(int x, int y, char c) { Set(x, y, c); Set(W - 1 - x, y, c); }

        // Body geometry per shape: centre, radii, top of head, eye line, bottom.
        var (cx, cy, rx, ry, top, eyeY, bottom) = Geometry(shape);

        // Ears, behind the body.
        if (part == Part.Ears) switch (s.PetEars)
        {
            case 1: case 6:
                // Pointed ears as real triangles, with a lighter inner ear.
                double tall = s.PetEars == 6 ? 2.5 : 0, apex = top - 6 - tall, baseY = top + 2.5;
                foreach (double ax in new[] { 9.5, 22.5 })
                    Fill(ax - 6, apex, ax + 6, baseY, (x, y) =>
                    {
                        double t = (y - apex) / (baseY - apex);
                        return t >= 0 && t <= 1 && Math.Abs(x - ax + (ax < 16 ? -t * 0.8 : t * 0.8)) <= t * 4.6;
                    }, 'B');
                foreach (double ax in new[] { 9.5, 22.5 })
                    Fill(ax - 6, apex, ax + 6, baseY, (x, y) =>
                    {
                        double t = (y - apex) / (baseY - apex);
                        return t >= 0.35 && t <= 1 && Math.Abs(x - ax + (ax < 16 ? -t * 0.8 : t * 0.8)) <= t * 4.6 - 1.6;
                    }, 'A');
                break;
            case 2:
                for (int side = -1; side <= 1; side += 2) { Ellipse(16 + side * 5, top - 4, 2.7, 7.2, 'B'); Ellipse(16 + side * 5, top - 3.2, 1.3, 5, 'A'); }
                break;
            case 3:
                for (int side = -1; side <= 1; side += 2) { Ellipse(16 + side * 8.5, top + 1, 3.7, 3.7, 'B'); Ellipse(16 + side * 8.5, top + 1.3, 1.9, 1.9, 'A'); }
                break;
            case 4:
                Fill(15, top - 7, 17, top, (x, y) => Math.Abs(x - 16) <= 0.5, 'D');
                Ellipse(16, top - 7.5, 2.2, 2.2, 'A');
                Ellipse(15.3, top - 8.2, 0.7, 0.7, 'v');
                break;
            case 5:
                // Little curved horns.
                foreach (int side in new[] { -1, 1 })
                    Fill(0, top - 7, W, top + 2, (x, y) =>
                    {
                        double t = (top + 1.5 - y) / 7.5;
                        double hx = 16 + side * (6.5 + t * 2.5 - t * t * 2);
                        return t >= 0 && t <= 1 && Math.Abs(x - hx) <= 1.6 * (1 - t * 0.7);
                    }, 'd');
                break;
        }

        // Body.
        if (part == Part.Body) Fill(0, top - 1, W, bottom + 1.5, (px, py) => shape switch
        {
            1 => px >= cx - rx && px <= cx + rx && py >= top && py <= bottom && !(Corner(px, py, cx - rx + 4, top + 4) || Corner(px, py, cx + rx - 4, top + 4) || Corner(px, py, cx - rx + 4, bottom - 4) || Corner(px, py, cx + rx - 4, bottom - 4)),
            3 => py <= 16 ? Sq((px - cx) / rx) + Sq((py - 16.5) / 11.5) <= 1 : px >= cx - rx && px <= cx + rx && py <= bottom - 1.6 + 1.6 * Math.Cos((px - 5) * Math.PI / 4 + pose.Wave * Math.PI),
            _ => Sq((px - cx) / rx) + Sq((py - cy) / ry) <= 1,
        }, 'B');
        bool Corner(double px, double py, double ccx, double ccy) =>
            ((px < ccx && ccx < cx) || (px > ccx && ccx > cx)) && ((py < ccy && ccy < cy) || (py > ccy && ccy > cy)) && Sq(px - ccx) + Sq(py - ccy) > 16;

        // Feet (walking lifts one) and little arms (up while dancing).
        // Feet and arms are parts of their own; they are lifted and swung while drawing.
        if (shape != 3 && !pose.Sit && part is Part.FootL or Part.FootR)
        {
            char foot = s.PetMouth == 6 ? 'N' : 'B'; // a beak comes with orange feet, like a penguin
            Ellipse(part == Part.FootL ? 11 : 21, bottom + 1, 3.1, 1.7, foot);
        }
        if (part == Part.ArmL) Ellipse(cx - rx - 0.3, cy + 2, 2.1, 1.6, 'B');
        if (part == Part.ArmR) Ellipse(cx + rx + 0.3, cy + 2, 2.1, 1.6, 'B');

        // Light from above: two light and two shade steps, dithered where they meet, and a darker rim at the bottom.
        var shaded = (char[,])g.Clone();
        for (int fy = 0; fy < FH; fy++)
            for (int fx = 0; fx < FW; fx++)
            {
                if (g[fy, fx] != 'B') continue;
                double nx = (GX(fx) - cx) / rx, ny = (GY(fy) - cy) / ry;
                double light = -0.3 * nx - 0.9 * ny;
                bool checker = (fx + fy) % 2 == 0;
                double Edge(double v, double at) => v - at + (checker ? 0.05 : -0.05); // dithering band around each step
                char c = s.PetShading switch
                {
                    0 => 'B',
                    1 => Edge(light, 0.6) > 0 ? 'L' : Edge(light, -0.55) < 0 ? 'S' : 'B',
                    _ => Edge(light, 0.95) > 0 ? 'l' : Edge(light, 0.45) > 0 ? 'L' : Edge(light, -0.75) < 0 ? 's' : Edge(light, -0.35) < 0 ? 'S' : 'B',
                };
                bool edgeBelow = fy + 1 >= FH || g[fy + 1, fx] == '\0';
                if (edgeBelow && s.PetShading > 0) c = 's';
                shaded[fy, fx] = c;
            }
        g = shaded;
        // A soft shine on the upper left of the body.
        if (s.PetShading > 0 && shape != 1 && part == Part.Body)
            Fill(cx - rx * 0.7, cy - ry * 0.9, cx - rx * 0.1, cy - ry * 0.3, (x, y) => Sq((x - (cx - rx * 0.42)) / 1.6) + Sq((y - (cy - ry * 0.62)) / 0.9) <= 1 && GetF((int)(x * S), (int)((y + OY) * S)) != '\0', 'l');

        // Pattern in the second color, keeping the shading.
        bool BodyF(int fx, int fy) => GetF(fx, fy) is 'B' or 'L' or 'l' or 'S' or 's';
        bool Body(int x, int y) => BodyF(x * S, (y + OY) * S);
        void PaintF(int fx, int fy) { if (BodyF(fx, fy)) SetF(fx, fy, GetF(fx, fy) is 'S' or 's' ? 'a' : GetF(fx, fy) is 'l' ? 'b' : 'A'); }
        void PaintWhere(Func<double, double, bool> inside)
        {
            for (int fy = 0; fy < FH; fy++) for (int fx = 0; fx < FW; fx++) if (inside(GX(fx), GY(fy))) PaintF(fx, fy);
        }
        if (part == Part.Body) switch (s.PetPattern)
        {
            case 1: PaintWhere((x, y) => Sq((x - cx) / (rx * 0.45)) + Sq((y - (cy + ry * 0.68)) / (ry * 0.3)) <= 1); break;
            case 2: PaintWhere((x, y) => (int)Math.Floor(y - top) % 3 == 0 && Math.Abs(x - cx) > rx * 0.45 - Math.Sin(y) * 0.6); break;
            case 3: PaintWhere((x, y) => new[] { (9.0, cy + 3, 2.4), (22.0, cy - 4, 2.0), (20.0, cy + 6, 1.6), (12.0, top + 3.0, 1.4) }.Any(sp => Sq(x - sp.Item1) + Sq(y - sp.Item2) <= sp.Item3 * sp.Item3)); break;
            case 4: PaintWhere((x, y) => y > cy + 2 + Math.Sin(x * 1.2) * 0.5); break;
            case 5: PaintWhere((x, y) => new[] { (9.0, cy + 3), (22.0, cy - 2), (13.0, top + 4.0), (20.0, cy + 6) }.Any(sp => (Math.Abs(x - sp.Item1) < 0.5 && Math.Abs(y - sp.Item2) < 1.6) || (Math.Abs(y - sp.Item2) < 0.5 && Math.Abs(x - sp.Item1) < 1.6))); break;
            case 6: // penguin mask: a heart-shaped face and a big belly in the second color
                PaintWhere((x, y) =>
                {
                    bool face = (Sq(x - (cx - 4)) + Sq(y - eyeY) <= 4.8 * 4.8 || Sq(x - (cx + 4)) + Sq(y - eyeY) <= 4.8 * 4.8) && y > eyeY - 4;
                    bool belly = Sq((x - cx) / (rx * 0.66)) + Sq((y - (cy + ry * 0.38)) / (ry * 0.58)) <= 1;
                    return face || belly;
                });
                break;
        }

        if (part == Part.Body)
        {
        // Face: eyes with a glint, drawn on the fine grid.
        int e1 = shape == 2 ? 12 : 11, e2 = shape == 2 ? 20 : 21, ey = eyeY;
        void Eye(int ex)
        {
            if (pose.Closed && s.PetEyes != 4)
            {
                // A curved closed lid.
                Fill(ex - 2.2, ey, ex + 2.2, ey + 2, (x, y) => Math.Abs(y - (ey + 1.6 - Sq(x - ex) * 0.12)) <= 0.5, 'E');
                return;
            }
            switch (s.PetEyes)
            {
                case 0:
                    Ellipse(ex, ey + 1, 1.25, 1.5, 'E');
                    SetF((int)((ex - 0.6) * S), (ey + OY + 0) * S + 1, 'W');
                    break;
                case 2:
                    Fill(ex - 2.2, ey, ex + 2.2, ey + 3, (x, y) => Sq((x - ex) / 2.1) + Sq((y - (ey + 1.6)) / 1.4) <= 1 && y > ey + 0.9, 'E');
                    Fill(ex - 2.4, ey, ex + 2.4, ey + 1, (x, y) => Math.Abs(y - (ey + 0.9)) <= 0.5 && Math.Abs(x - ex) <= 2.3, 'S');
                    break;
                case 3:
                    Fill(ex - 2.5, ey - 1, ex + 2.5, ey + 2, (x, y) => Math.Abs(y - (ey + 0.2 + Sq(x - ex) * 0.22)) <= 0.5 && Math.Abs(x - ex) <= 2.1, 'E');
                    break;
                case 5:
                    Fill(ex - 2.6, ey - 2.6, ex + 2.6, ey + 2.6, (x, y) => Math.Abs(x - ex) + Math.Abs(y - ey) * 0.9 <= 2.4 && (Math.Abs(x - ex) < 0.8 || Math.Abs(y - ey) < 0.8 || Math.Abs(x - ex) + Math.Abs(y - ey) <= 1.6), 'Y');
                    Ellipse(ex, ey, 0.5, 0.5, 'W');
                    break;
                default:
                    // Big round eyes: dark with a lighter lower half, a large glint top left and a small one bottom right.
                    Ellipse(ex - 0.5, ey, 2.25, 2.7, 'E');
                    Fill(ex - 3, ey, ex + 2, ey + 3, (x, y) => Sq((x - (ex - 0.5)) / 1.6) + Sq((y - (ey + 1.3)) / 1.1) <= 1, 'e');
                    Ellipse(ex - 1.3, ey - 1.1, 0.9, 0.9, 'W');
                    SetF((int)((ex + 0.6) * S), (int)((ey + 1.3 + OY) * S), 'W');
                    break;
            }
        }
        if (s.PetEyes == 4)
        {
            Fill(7, ey - 2, 25, ey + 2, (x, y) => x >= 7.2 && x <= 24.8 && y >= ey - 2 && y <= ey + 1.6 && !(Sq(x - 16) < 0.3 && y > ey + 0.8), 'V');
            Fill(8, ey - 2, 24, ey - 1, (x, y) => y < ey - 1.3 && x > 8 && x < 24, 'v');
            if (pose.Closed) Rect(8, ey, 24, ey, 'a');
            else { Ellipse(10.5, ey - 0.8, 1, 0.6, 'W'); Ellipse(12.5, ey - 0.8, 0.4, 0.4, 'W'); }
        }
        else { Eye(e1); Eye(e2); }
        // Half-closed lids, for a soft blink.
        if (pose.Half && !pose.Closed && s.PetEyes != 4)
            foreach (int ex in new[] { e1, e2 })
                Fill(ex - 3.5, ey - 3.5, ex + 3, ey + 0.4, (x, y) => y < ey + 0.3 && GetF((int)(x * S), (int)((y + OY) * S)) is 'E' or 'e' or 'W' or 'Y', 'S');

        // Mouth on the fine grid: soft curves instead of steps.
        double my = ey + (s.PetEyes == 4 ? 4 : 5);
        int mouth = pose.Open && s.PetMouth != 0 && s.PetMouth != 6 ? 4 : s.PetMouth;
        double mx = 16.5;
        switch (mouth)
        {
            case 1: case 3: case 5:
                Fill(mx - 3, my - 1, mx + 3, my + 2, (x, y) => Math.Abs(y - (my + 1.1 - Sq(x - mx) * 0.13)) <= 0.45 && Math.Abs(x - mx) <= 2.3, 'E');
                if (mouth == 3) { Ellipse(mx - 1, my + 1.9, 0.45, 0.6, 'W'); Ellipse(mx + 1, my + 1.9, 0.45, 0.6, 'W'); }
                if (mouth == 5) Ellipse(mx + 0.4, my + 2.1, 0.9, 1, 'P');
                break;
            case 2:
                Fill(mx - 3, my - 1, mx + 3, my + 2, (x, y) => Math.Abs(y - (my + 0.9 - Math.Abs(Math.Sin((x - mx) * 1.35)) * 0.8)) <= 0.45 && Math.Abs(x - mx) <= 2.4, 'E');
                break;
            case 4:
                Ellipse(mx, my + 1, 1.5, 1.6, 'E');
                Ellipse(mx, my + 1.6, 0.9, 0.7, 'P');
                break;
            case 6:
                // Beak: lighter top, darker underside.
                Fill(13, ey + 1.5, 20, ey + 4.5, (x, y) => Math.Abs(x - mx) <= 2.8 - (y - (ey + 1.8)) * 0.9 && y >= ey + 1.8 && y <= ey + 4.3, 'N');
                Fill(13, ey + 3, 20, ey + 4.5, (x, y) => Math.Abs(x - mx) <= 2.8 - (y - (ey + 1.8)) * 0.9 && y >= ey + 3.2 && y <= ey + 4.3, 'n');
                break;
        }
        if (s.PetCheeks) { Ellipse(e1 - 2.8, my, 1.6, 0.8, 'K'); Ellipse(e2 + 2.8, my, 1.6, 0.8, 'K'); }

        // Accessories around the face and neck.
        int neck = (int)my + 3;
        switch (s.PetAccessory)
        {
            case 1: // round glasses and a bridge
                foreach (int ex in new[] { e1, e2 })
                    Fill(ex - 4, ey - 4, ex + 4, ey + 4, (x, y) => { double d = Math.Sqrt(Sq(x - (ex - 0.5)) + Sq(y - ey)); return d <= 3.3 && d >= 2.6; }, 'D');
                Fill(e1 + 2, ey - 1, e2 - 3, ey, (x, y) => Math.Abs(y - (ey - 0.6)) <= 0.4 && x > e1 + 2.6 && x < e2 - 3.4, 'D');
                break;
            case 2: // sunglasses with a shine
                foreach (int ex in new[] { e1, e2 })
                {
                    Fill(ex - 4, ey - 3, ex + 3, ey + 3, (x, y) => Sq((x - (ex - 0.5)) / 2.9) + Sq((y - ey) / 2.3) <= 1, 'D');
                    Fill(ex - 3, ey - 2, ex, ey, (x, y) => Math.Abs((x - ex) + (y - ey) + 1.5) <= 0.4 && Sq((x - (ex - 0.5)) / 2.4) + Sq((y - ey) / 1.9) <= 1, 'd');
                }
                Fill(e1 + 2, ey - 1, e2 - 3, ey, (x, y) => Math.Abs(y - (ey - 0.6)) <= 0.4, 'D');
                break;
            case 3: // knitted scarf with a hanging end
                for (int fy = (neck + OY) * S; fy < (neck + 2 + OY) * S; fy++) for (int fx = 0; fx < FW; fx++) if (BodyF(fx, fy) || GetF(fx, fy) is 'A' or 'a') SetF(fx, fy, (fx / 2 + fy / 2) % 3 == 0 ? 'r' : 'R');
                Rect(20, neck + 2, 22, neck + 6, 'R'); Rect(20, neck + 4, 22, neck + 4, 'F'); Fill(20, neck + 6, 23, neck + 7, (x, y) => (int)(x * 2) % 2 == 0, 'R');
                break;
            case 4: // bow tie with a knot
                Fill(12, neck - 2, 21, neck + 4, (x, y) => { double dx = Math.Abs(x - 16.5), dy = Math.Abs(y - (neck + 1.5)); return dx <= 4 && dy <= 0.6 + dx * 0.45; }, 'R');
                Fill(12, neck - 2, 21, neck + 4, (x, y) => { double dx = Math.Abs(x - 16.5), dy = Math.Abs(y - (neck + 1.5)); return dx > 2 && dx <= 4 && dy <= 0.6 + dx * 0.45 && y > neck + 1.5; }, 'r');
                Ellipse(16.5, neck + 1.5, 0.9, 0.9, 'D');
                break;
            case 5: // collar with a bell
                for (int fy = (neck + OY) * S; fy < (neck + 1 + OY) * S; fy++) for (int fx = 0; fx < FW; fx++) if (BodyF(fx, fy) || GetF(fx, fy) is 'A' or 'a') SetF(fx, fy, 'R');
                Ellipse(16.5, neck + 2, 1.6, 1.6, 'G'); Ellipse(16, neck + 1.5, 0.5, 0.5, 'Y'); Fill(15, neck + 2.6, 18, neck + 3.4, (x, y) => Math.Abs(x - 16.5) < 0.5, 'g');
                break;
        }

        }
        // Hat on top.
        if (part == Part.Hat) switch (s.PetHat)
        {
            case 1: // cap with brim
                Fill(7, top - 5, 26, top, (x, y) => Sq((x - 16) / 8.6) + Sq((y - top) / 4.6) <= 1 && y <= top - 0.2, 'H');
                Fill(22, top - 1.2, 30, top + 0.2, (x, y) => y >= top - 1.1 && y <= top - 0.1 && x <= 29.5 - (top - y) * 0.5, 'H');
                Fill(9, top - 4, 15, top - 2, (x, y) => Sq((x - 12) / 2.2) + Sq((y - (top - 3)) / 0.8) <= 1, 'h');
                Ellipse(16, top - 4.8, 0.7, 0.5, 'h');
                break;
            case 2: // crown with jewels
                Fill(9, top - 7, 23, top, (x, y) =>
                {
                    if (x < 9 || x > 23 || y > top - 0.2) return false;
                    if (y >= top - 3) return true;
                    double phase = (x - 9) / 14 * 4; // four points
                    double peak = Math.Abs(phase - Math.Round(phase));
                    return y >= top - 3 - (1 - peak * 2) * 3.6;
                }, 'G');
                Fill(9, top - 1, 23, top, (x, y) => y > top - 1 && x >= 9 && x <= 23, 'g');
                Ellipse(12.5, top - 2, 0.8, 0.8, 'R'); Ellipse(19.5, top - 2, 0.8, 0.8, 'R'); Ellipse(16, top - 2, 1, 1, 'J');
                break;
            case 3: // headphones with a padded band
                Fill(0, top - 6, W, eyeY - 2, (x, y) => { double d = Math.Sqrt(Sq((x - 16) / 13.5) + Sq((y - (eyeY + 1)) / (eyeY + 1 - top + 5))); return d > 0.92 && d <= 1.02; }, 'D');
                Fill(0, eyeY - 4, 5, eyeY + 4, (x, y) => Sq((x - 2.6) / 2) + Sq((y - eyeY) / 3.6) <= 1, 'D');
                Fill(27, eyeY - 4, W, eyeY + 4, (x, y) => Sq((x - 29.4) / 2) + Sq((y - eyeY) / 3.6) <= 1, 'D');
                Fill(0, eyeY - 3, 5, eyeY + 3, (x, y) => Sq((x - 2.8) / 1) + Sq((y - eyeY) / 2.6) <= 1, 'H');
                Fill(27, eyeY - 3, W, eyeY + 3, (x, y) => Sq((x - 29.2) / 1) + Sq((y - eyeY) / 2.6) <= 1, 'H');
                break;
            case 4: // knitted beanie with a pom-pom
                Fill(6, top - 6, 26, top + 1.5, (x, y) => Sq((x - 16) / 9.2) + Sq((y - (top + 1)) / 6.6) <= 1 && y <= top + 1.3, 'H');
                Fill(6, top - 1, 26, top + 1.5, (x, y) => y > top - 0.6 && y <= top + 1.3 && Math.Abs(x - 16) <= 9.4, 'h');
                Fill(6, top - 6, 26, top, (x, y) => (int)Math.Floor(x * 2) % 3 == 0 && y < top - 0.6 && Sq((x - 16) / 9.2) + Sq((y - (top + 1)) / 6.6) <= 1, 'b');
                Ellipse(16, top - 6.6, 2.4, 2.2, 'F');
                break;
            case 5: // halo
                Fill(7, top - 8, 25, top - 2, (x, y) => { double d = Sq((x - 16) / 7.5) + Sq((y - (top - 5)) / 1.9); return d <= 1 && d > 0.38; }, 'G');
                Fill(7, top - 8, 25, top - 2, (x, y) => { double d = Sq((x - 16) / 7.5) + Sq((y - (top - 5)) / 1.9); return d <= 1 && d > 0.38 && y < top - 5.6; }, 'Y');
                break;
            case 6: // flower
                foreach (var (dx, dy) in new[] { (0.0, -1.8), (0.0, 1.8), (-1.8, 0.0), (1.8, 0.0), (-1.3, -1.3), (1.3, -1.3), (-1.3, 1.3), (1.3, 1.3) }) Ellipse(23 + dx, top - 1 + dy, 1.1, 1.1, 'P');
                Ellipse(23, top - 1, 1, 1, 'Y'); Ellipse(20.6, top + 1.5, 1.4, 0.6, 'L');
                break;
            case 7: // bow
                Fill(17, top - 3, 28, top + 2, (x, y) => { double dx = Math.Abs(x - 22.5), dy = Math.Abs(y - (top - 0.5)); return dx <= 4.2 && dy <= 0.7 + dx * 0.55; }, 'P');
                Fill(17, top - 3, 28, top + 2, (x, y) => { double dx = Math.Abs(x - 22.5), dy = Math.Abs(y - (top - 0.5)); return dx > 2.5 && dx <= 4.2 && dy <= 0.7 + dx * 0.55 && y > top - 0.5; }, 'r');
                Ellipse(22.5, top - 0.5, 1, 1, 'R');
                break;
            case 8: // wizard hat with a star
                Fill(8, top - 12, 26, top, (x, y) => { double t = (y - (top - 11.5)) / 11; double hx = 18 - t * 2 + Math.Sin(t * 3) * 0.6; return t >= 0 && t <= 1 && Math.Abs(x - hx) <= 0.5 + t * 6; }, 'U');
                Fill(8, top - 12, 26, top, (x, y) => { double t = (y - (top - 11.5)) / 11; double hx = 18 - t * 2 + Math.Sin(t * 3) * 0.6; return t >= 0.2 && t <= 1 && x - hx > t * 3 && x - hx <= 0.5 + t * 6; }, 'u');
                Fill(5, top - 1.5, 27, top + 0.5, (x, y) => Sq((x - 16) / 10.5) + Sq((y - (top - 0.4)) / 1.1) <= 1, 'u');
                Fill(13, top - 7, 19, top - 2, (x, y) => Math.Abs(x - 16) + Math.Abs(y - (top - 4.5)) * 0.9 <= 1.9 && (Math.Abs(x - 16) < 0.6 || Math.Abs(y - (top - 4.5)) < 0.6 || Math.Abs(x - 16) + Math.Abs(y - (top - 4.5)) < 1.2), 'Y');
                break;
        }

        // Outline one fine pixel wide: darker where the shape is in shade, a bit softer on the lit top edge.
        if (s.PetOutline)
        {
            var outlined = (char[,])g.Clone();
            for (int fy = 0; fy < FH; fy++)
                for (int fx = 0; fx < FW; fx++)
                {
                    if (g[fy, fx] != '\0') continue;
                    bool below = fy < FH - 1 && g[fy + 1, fx] != '\0';
                    bool near = below || (fx > 0 && g[fy, fx - 1] != '\0') || (fx < FW - 1 && g[fy, fx + 1] != '\0') || (fy > 0 && g[fy - 1, fx] != '\0');
                    if (near) outlined[fy, fx] = below && !(fy > 0 && g[fy - 1, fx] != '\0') && GY(fy) < cy - ry * 0.4 ? 'o' : 'O';
                }
            g = outlined;
        }
        return g;
    }
}
