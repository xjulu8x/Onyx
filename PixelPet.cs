using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopOverlay;

/// <summary>
/// A small pixel character on the start page of the island. It wanders, blinks, dances while music plays and hops when
/// clicked. Kind, color, hat and name are set under Einstellungen → Spiel. It only moves while the island is open.
/// </summary>
public sealed class PixelPet : FrameworkElement
{
    public static readonly string[] Kinds = { "Katze", "Geist", "Bot" };
    public static readonly string[] Hats = { "Keiner", "Kappe", "Krone", "Kopfhörer" };
    public static readonly string[] Colors = { "#E6E6E6", "#FFB340", "#30D158", "#FF6B8A", "#7FA6F0" };
    public static readonly string[] ColorNames = { "Weiß", "Bernstein", "Grün", "Rosa", "Blau" };

    // Pixel maps: X body, D dark, W white, P pink, . empty. Two frames each for walking.
    private static readonly string[][][] Sprites =
    {
        new[]
        {
            new[] { ".X........X.", ".XX......XX.", ".XXXXXXXXXX.", "XXXXXXXXXXXX", "XXWDXXXXWDXX", "XXXXXPPXXXXX", ".XXXXXXXXXX.", ".XXXXXXXXXX.", ".XX.....XX.." },
            new[] { ".X........X.", ".XX......XX.", ".XXXXXXXXXX.", "XXXXXXXXXXXX", "XXWDXXXXWDXX", "XXXXXPPXXXXX", ".XXXXXXXXXX.", ".XXXXXXXXXX.", "..XX...XX..." },
        },
        new[]
        {
            new[] { "...XXXXXX...", "..XXXXXXXX..", ".XXXXXXXXXX.", ".XXWDXXWDXX.", ".XXXXXXXXXX.", ".XXXXPPXXXX.", ".XXXXXXXXXX.", ".XXXXXXXXXX.", ".X.XX.XX.XX." },
            new[] { "...XXXXXX...", "..XXXXXXXX..", ".XXXXXXXXXX.", ".XXWDXXWDXX.", ".XXXXXXXXXX.", ".XXXXPPXXXX.", ".XXXXXXXXXX.", ".XXXXXXXXXX.", ".XX.XX.XX.X." },
        },
        new[]
        {
            new[] { "..XXXXXXXX..", "..XWDXXWDX..", "..XXXXXXXX..", "..XXDDDDXX..", "...XXXXXX...", ".XXXXXXXXXX.", ".X.XXXXXX.X.", "...XXXXXX...", "...XX..XX..." },
            new[] { "..XXXXXXXX..", "..XWDXXWDX..", "..XXXXXXXX..", "..XXDDDDXX..", "...XXXXXX...", ".XXXXXXXXXX.", "X..XXXXXX..X", "...XXXXXX...", "..XX....XX.." },
        },
    };
    private static readonly int[] EyeRow = { 4, 3, 1 };

    private const double Px = 4; // one sprite pixel
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(125) };
    private readonly Random _random = new();
    private readonly List<(Point At, double Life, bool Heart)> _bits = new();
    private double _x = 40, _target = 40, _hop, _hopSpeed;
    private int _frame, _blink, _facing = 1, _idle;

    public PixelPet()
    {
        _timer.Tick += (_, _) => Step();
        Cursor = Cursors.Hand;
        MouseLeftButtonUp += (_, _) => { if (_hop <= 0) _hopSpeed = 7; _bits.Add((new Point(_x + 24, 10), 1, true)); };
    }

    public bool Running
    {
        set { if (value) _timer.Start(); else _timer.Stop(); }
    }

    protected override Size MeasureOverride(Size available) => new(Math.Min(available.Width, 400), 80);

    private void Step()
    {
        _frame++;
        var media = MediaService.Instance;
        bool dancing = media.IsPlaying;
        double floor = ActualWidth - 12 * Px;
        if (dancing)
        {
            // Dance: little hops to the music and notes floating up.
            double level = Math.Sqrt(Volume.Peak());
            if (_hop <= 0 && level > 0.35) _hopSpeed = 3 + level * 4;
            if (_frame % 6 == 0) _bits.Add((new Point(_x + _random.Next(8, 40), 14), 1, false));
        }
        else if (--_idle <= 0)
        {
            // Wander: pick a new spot now and then, rest in between.
            _target = _random.NextDouble() * Math.Max(1, floor);
            _idle = 20 + _random.Next(60);
        }
        if (Math.Abs(_target - _x) > 2 && !dancing)
        {
            _facing = Math.Sign(_target - _x);
            _x += _facing * 2;
        }
        _x = Math.Clamp(_x, 0, Math.Max(0, floor));
        if (_hopSpeed > 0 || _hop > 0) { _hop += _hopSpeed; _hopSpeed -= 1.6; if (_hop <= 0) { _hop = 0; _hopSpeed = 0; } }
        if (--_blink < -30 - _random.Next(40)) _blink = 2;
        for (int i = _bits.Count - 1; i >= 0; i--)
        {
            var (at, life, heart) = _bits[i];
            life -= 0.06;
            if (life <= 0) _bits.RemoveAt(i); else _bits[i] = (new Point(at.X + Math.Sin(life * 9) * 0.6, at.Y - 1.5), life, heart);
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var s = Settings.Current;
        int kind = Math.Clamp(s.PetKind, 0, Sprites.Length - 1);
        bool walking = Math.Abs(_target - _x) > 2 || MediaService.Instance.IsPlaying;
        var sprite = Sprites[kind][walking && _frame / 2 % 2 == 1 ? 1 : 0];
        var body = Brush(PixelPet.Colors.Contains(s.PetColor) ? s.PetColor : "#E6E6E6");
        var dark = Brush("#0A0A0A");
        var white = Brush("#FFFFFF");
        var pink = Brush("#FF6B8A");
        double baseY = ActualHeight - sprite.Length * Px - 2;
        double y0 = baseY - _hop;

        // Floor: a dotted line like the dot grid.
        var dot = Brush("#33FFFFFF");
        for (double x = 0; x < ActualWidth; x += 8) dc.DrawRectangle(dot, null, new Rect(x, ActualHeight - 1, 2, 1));
        // Shadow shrinks while hopping.
        double shadow = Math.Max(4, 10 - _hop / 3) * Px / 2;
        dc.DrawRectangle(Brush("#22FFFFFF"), null, new Rect(_x + 6 * Px - shadow, ActualHeight - 3, shadow * 2, 2));

        for (int r = 0; r < sprite.Length; r++)
            for (int c = 0; c < 12; c++)
            {
                char ch = sprite[r][_facing < 0 ? 11 - c : c];
                if (ch == '.') continue;
                if (_blink > 0 && r == EyeRow[kind] && ch is 'W' or 'D') ch = 'X';
                var b = ch switch { 'D' => dark, 'W' => white, 'P' => pink, _ => body };
                dc.DrawRectangle(b, null, new Rect(_x + c * Px, y0 + r * Px, Px, Px));
            }
        DrawHat(dc, s.PetHat, kind, y0);

        foreach (var (at, life, heart) in _bits)
        {
            var b = heart ? Brush("#FF6B8A") : body;
            dc.PushOpacity(life);
            if (heart) { Pix(dc, b, at, 1, 0); Pix(dc, b, at, 3, 0); for (int i = 0; i < 5; i++) Pix(dc, b, at, i, 1); Pix(dc, b, at, 1, 2); Pix(dc, b, at, 2, 2); Pix(dc, b, at, 3, 2); Pix(dc, b, at, 2, 3); }
            else { Pix(dc, b, at, 2, 0); Pix(dc, b, at, 3, 0); Pix(dc, b, at, 2, 1); Pix(dc, b, at, 2, 2); Pix(dc, b, at, 1, 3); Pix(dc, b, at, 2, 3); }
            dc.Pop();
        }
    }

    private static void Pix(DrawingContext dc, Brush b, Point at, int c, int r) => dc.DrawRectangle(b, null, new Rect(at.X + c * 2, at.Y + r * 2, 2, 2));

    private void DrawHat(DrawingContext dc, int hat, int kind, double y0)
    {
        string[] rows = hat switch
        {
            1 => new[] { "..CCCCCCC...", "..CCCCCCCCCC" },
            2 => new[] { "..Y.Y..Y.Y..", "..YYYYYYYY.." },
            3 => new[] { "...HHHHHH...", "..H......H..", ".H........H." },
            _ => Array.Empty<string>(),
        };
        var cap = Theme.AccentBrush;
        var gold = Brush("#FFC940");
        var phones = Brush("#3A3A3A");
        double top = y0 - (hat == 3 ? 1 : rows.Length) * Px;
        for (int r = 0; r < rows.Length; r++)
            for (int c = 0; c < 12; c++)
            {
                char ch = rows[r][_facing < 0 ? 11 - c : c];
                if (ch == '.') continue;
                var b = ch switch { 'Y' => gold, 'H' => phones, _ => cap };
                dc.DrawRectangle(b, null, new Rect(_x + c * Px, top + r * Px, Px, Px));
            }
    }

    private static readonly Dictionary<string, Brush> Brushes = new();

    private static Brush Brush(string hex)
    {
        if (Brushes.TryGetValue(hex, out var b)) return b;
        b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return Brushes[hex] = b;
    }
}
