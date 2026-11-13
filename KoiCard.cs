using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopOverlay;

/// <summary>A quiet koi pond drawn in dots: fish wander, follow food, flee from ripples; petals drift in spring mode.</summary>
public sealed class KoiCard : Card
{
    private readonly Pond _pond = new() { Height = 196 };
    private readonly TextBlock _mode = Mono("", 8.5, 0.45);

    public KoiCard() : base("Koi", "+ Koi / 01", 440, 300)
    {
        Meta.Children.Add(_mode);
        var water = new Border { BorderBrush = Theme.Res("Line"), BorderThickness = new Thickness(1), Child = _pond, ClipToBounds = true, Cursor = Cursors.Hand };
        _pond.MouseLeftButtonDown += (_, e) => _pond.Ripple(e.GetPosition(_pond));
        var keys = new UniformGrid { Columns = 5, Margin = new Thickness(0, 8, -4, 0) };
        keys.Children.Add(Hotkey("F", "Füttern", () => _pond.Feed()));
        keys.Children.Add(Hotkey("C", "Tempo", () => { _pond.Fast = !_pond.Fast; ShowMode(); }));
        keys.Children.Add(Hotkey("S", "Sakura", () => { _pond.Sakura = !_pond.Sakura; ShowMode(); }));
        keys.Children.Add(Hotkey("H", "Farbe", () => { _pond.Mono = !_pond.Mono; ShowMode(); }));
        keys.Children.Add(Hotkey("D", "Tiefe", () => { _pond.Deep = !_pond.Deep; ShowMode(); }));
        var stack = new StackPanel();
        stack.Children.Add(water);
        stack.Children.Add(keys);
        SetBody(stack);
        ShowMode();
        Every(TimeSpan.FromMilliseconds(83), _pond.Step); // 12 frames per second
    }

    private void ShowMode() =>
        _mode.Text = $"{_pond.Count} KOI  /  {(_pond.Fast ? "FLINK" : "RUHIG")}  /  {(_pond.Sakura ? "SAKURA" : "STILL")}";

    protected override Point DefaultPlace(Rect w) => new(w.Right - 456, w.Bottom - 312);
}

public sealed class Pond : FrameworkElement
{
    private sealed class Fish
    {
        public Point Head;
        public double Angle, Speed, Turn, Size;
        public readonly List<Point> Trail = new();
        public bool[] Spots;
    }

    private readonly List<Fish> _fish = new();
    private readonly List<(Point At, double Radius)> _ripples = new();
    private readonly List<Point> _food = new();
    private readonly List<(Point At, double Drift)> _petals = new();
    private readonly Random _random = new();
    private static readonly Brush Body = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)));
    private static readonly Brush Spot = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x3C)));
    private static readonly Brush Faint = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0xE6, 0xE6, 0xE6)));
    private static readonly Brush Petal = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xB8, 0xC6)));
    private static readonly Brush Water = Freeze(new SolidColorBrush(Color.FromArgb(0x10, 0x60, 0x80, 0x90)));
    private static readonly Pen Ring = FreezePen(new Pen(new SolidColorBrush(Color.FromArgb(0x50, 255, 255, 255)), 1) { DashStyle = new DashStyle(new double[] { 1, 3 }, 0) });

    public bool Fast, Sakura, Mono, Deep = true;
    public int Count => _fish.Count;

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    private static Pen FreezePen(Pen p) { p.Freeze(); return p; }

    public Pond()
    {
        for (int i = 0; i < 4; i++)
            _fish.Add(new Fish
            {
                Head = new Point(70 + i * 90, 50 + i % 2 * 80), Angle = _random.NextDouble() * Math.PI * 2, Speed = 1.3 + _random.NextDouble() * 0.5,
                Size = 1.35 + _random.NextDouble() * 0.45, Spots = Enumerable.Range(0, 16).Select(_ => _random.NextDouble() < 0.4).ToArray(),
            });
    }

    public void Ripple(Point at)
    {
        _ripples.Add((at, 2));
        // Fish close to the ripple turn away from it.
        foreach (var f in _fish)
            if ((f.Head - at).Length < 90) f.Angle = Math.Atan2(f.Head.Y - at.Y, f.Head.X - at.X);
    }

    public void Feed()
    {
        for (int i = 0; i < 5; i++) _food.Add(new Point(40 + _random.NextDouble() * Math.Max(1, ActualWidth - 80), 30 + _random.NextDouble() * Math.Max(1, ActualHeight - 60)));
    }

    public void Step()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10) return;
        foreach (var f in _fish)
        {
            // Wander: the turn rate drifts slowly; food pulls, the edges push back.
            f.Turn = Math.Clamp(f.Turn + (_random.NextDouble() - 0.5) * 0.04, -0.06, 0.06);
            double target = f.Angle + f.Turn;
            if (_food.Count > 0)
            {
                var near = _food.OrderBy(p => (p - f.Head).Length).First();
                target = Steer(f.Angle, Math.Atan2(near.Y - f.Head.Y, near.X - f.Head.X), 0.08);
                if ((near - f.Head).Length < 8) { _food.Remove(near); _ripples.Add((near, 1)); }
            }
            const double margin = 30;
            if (f.Head.X < margin || f.Head.X > w - margin || f.Head.Y < margin || f.Head.Y > h - margin)
                target = Steer(f.Angle, Math.Atan2(h / 2 - f.Head.Y, w / 2 - f.Head.X), 0.07);
            f.Angle = target;
            double speed = f.Speed * (Fast ? 1.9 : 1);
            f.Head = new Point(f.Head.X + Math.Cos(f.Angle) * speed, f.Head.Y + Math.Sin(f.Angle) * speed);
            f.Trail.Insert(0, f.Head);
            if (f.Trail.Count > 30) f.Trail.RemoveAt(f.Trail.Count - 1);
        }
        for (int i = _ripples.Count - 1; i >= 0; i--)
        {
            var (at, r) = _ripples[i];
            if (r > 46) _ripples.RemoveAt(i); else _ripples[i] = (at, r + 1.6);
        }
        if (Sakura && _random.NextDouble() < 0.08) _petals.Add((new Point(_random.NextDouble() * w, -4), _random.NextDouble() * 0.6 - 0.3));
        for (int i = _petals.Count - 1; i >= 0; i--)
        {
            var (at, drift) = _petals[i];
            at = new Point(at.X + drift + Math.Sin(at.Y / 14) * 0.4, at.Y + 0.45);
            if (at.Y > h + 4 || !Sakura && _random.NextDouble() < 0.05) _petals.RemoveAt(i); else _petals[i] = (at, drift);
        }
        InvalidateVisual();
    }

    private static double Steer(double from, double to, double rate)
    {
        double d = Math.IEEERemainder(to - from, Math.PI * 2);
        return from + Math.Clamp(d, -rate, rate);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Water, null, new Rect(0, 0, ActualWidth, ActualHeight));
        foreach (var (at, r) in _ripples) dc.DrawEllipse(null, Ring, at, r, r * 0.55);
        foreach (var p in _food) dc.DrawRectangle(Faint, null, new Rect(p.X - 1, p.Y - 1, 2, 2));
        foreach (var f in _fish)
        {
            // The body is a chain of dot rows across the path: wide behind the head, thin at the tail.
            for (int s = 0; s < f.Trail.Count; s += 2)
            {
                var at = f.Trail[s];
                var next = f.Trail[Math.Min(s + 1, f.Trail.Count - 1)];
                double dir = Math.Atan2(at.Y - next.Y, at.X - next.X), across = dir + Math.PI / 2;
                double t = s / (double)f.Trail.Count;
                double half = f.Size * (t < 0.25 ? 3 + t * 12 : 6 * (1 - t) + 0.6);
                int index = s / 2;
                var brush = !Mono && index < f.Spots.Length && f.Spots[index] ? Spot : Body;
                if (Deep && t > 0.6) brush = Faint;
                for (double k = -half; k <= half + 0.01; k += 2.8)
                {
                    var dot = new Point(at.X + Math.Cos(across) * k, at.Y + Math.Sin(across) * k);
                    dc.DrawRectangle(brush, null, new Rect(dot.X - 1.1, dot.Y - 1.1, 2.2, 2.2));
                }
                // Side fins a little behind the head.
                if (s == 6)
                    for (int side = -1; side <= 1; side += 2)
                        for (int d = 1; d <= 3; d++)
                        {
                            var fin = new Point(at.X + Math.Cos(across) * side * (half + d * 2.2) - Math.Cos(dir) * d * 1.5, at.Y + Math.Sin(across) * side * (half + d * 2.2) - Math.Sin(dir) * d * 1.5);
                            dc.DrawRectangle(Faint, null, new Rect(fin.X - 0.8, fin.Y - 0.8, 1.6, 1.6));
                        }
            }
        }
        foreach (var (at, _) in _petals) dc.DrawRectangle(Petal, null, new Rect(at.X - 1.2, at.Y - 1.2, 2.4, 2.4));
    }
}
