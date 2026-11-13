using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace DesktopOverlay;

/// <summary>
/// An ASCII bonsai that grows out of its pot, sways in the wind and wants water now and then.
/// The tree comes from a seed, so the same tree comes back after a restart; "NEU" plants another one.
/// </summary>
public sealed class BonsaiCard : Card
{
    private const int Cols = 44, Rows = 25, PotRows = 4, Ground = Rows - PotRows - 1;
    private static readonly string[] Species = { "Juniper", "Ahorn", "Kiefer", "Ulme", "Kirsche" };
    private static readonly string[] Pot =
    {
        ":___________./~~~\\.___________:",
        " \\                           / ",
        "  \\_________________________/  ",
        "  (_)                     (_)  ",
    };

    private readonly char[,] _wood = new char[Rows, Cols], _leaves = new char[Rows, Cols];
    private readonly TextBlock _woodText = Screen(9), _leafText = Screen(9);
    private readonly TextBlock _stats = Mono("", 9, 0.75), _seedTag = Theme.Tag("");
    private readonly List<Branch> _growing = new();
    private Random _random;
    private int _frame, _steps;
    private bool _swayLeft;

    private sealed class Branch
    {
        public int X, Y, Life, Kind; // kind: 0 trunk, 1 shoot to the left, 2 shoot to the right
        public int Age;
    }

    public BonsaiCard() : base("Bonsai", "", 300, 430)
    {
        var s = Settings.Current;
        if (s.BonsaiSeed == 0) { s.BonsaiSeed = new Random().Next(0x1000, 0xFFFF); s.BonsaiPlanted = DateTime.Now; s.BonsaiWatered = DateTime.Now; s.Save(); }
        Meta.Children.Add(_seedTag);

        var scene = new Grid { Margin = new Thickness(0, 2, 0, 8), HorizontalAlignment = HorizontalAlignment.Center };
        _woodText.Opacity = 0.7;
        scene.Children.Add(_woodText);
        scene.Children.Add(_leafText);
        var sceneFrame = new Border { BorderBrush = Theme.Res("Line"), BorderThickness = new Thickness(1), Padding = new Thickness(4, 6, 4, 2), Child = scene };

        var keys = new UniformGrid { Columns = 3, Margin = new Thickness(0, 8, -4, 0) };
        keys.Children.Add(Hotkey("W", "Gießen", Water));
        keys.Children.Add(Hotkey("P", "Schneiden", Prune));
        keys.Children.Add(Hotkey("F", "Düngen", Feed));
        keys.Children.Add(Hotkey("B", "Wind", () => { s.BonsaiWind = !s.BonsaiWind; s.Save(); ShowStats(); }));
        keys.Children.Add(Hotkey("S", "Winter", () => { s.BonsaiWinter = !s.BonsaiWinter; s.Save(); Draw(); ShowStats(); }));
        keys.Children.Add(Hotkey("N", "Neu", Replant));

        var stack = new StackPanel();
        stack.Children.Add(sceneFrame);
        stack.Children.Add(_stats);
        stack.Children.Add(keys);
        SetBody(stack);
        Plant(grow: false);
        Every(TimeSpan.FromMilliseconds(125), Tick);
    }

    // ---------- growing ----------

    private void Plant(bool grow)
    {
        var s = Settings.Current;
        _random = new Random(s.BonsaiSeed);
        Ascii.Clear(_wood);
        Ascii.Clear(_leaves);
        for (int i = 0; i < Pot.Length; i++)
        {
            int left = (Cols - Pot[i].Length) / 2;
            for (int c = 0; c < Pot[i].Length; c++) _wood[Ground + 1 + i, left + c] = Pot[i][c];
        }
        _growing.Clear();
        _growing.Add(new Branch { X = Cols / 2, Y = Ground, Life = 15 + _random.Next(6), Kind = 0 });
        _steps = 0;
        Caption.Text = "+ " + Species[s.BonsaiSeed % Species.Length].ToUpperInvariant();
        _seedTag.Text = $"[{s.BonsaiSeed:X4}]  ALTER: {Math.Max(0, (int)(DateTime.Now - s.BonsaiPlanted).TotalDays)} T";
        if (!grow) while (_growing.Count > 0) Step(); // after a restart the tree is simply there
        Draw();
        ShowStats();
    }

    /// <summary>One step for every growing branch, the way terminal bonsai generators do it.</summary>
    private void Step()
    {
        _steps++;
        foreach (var b in _growing.ToArray())
        {
            b.Age++;
            int dx, dy;
            switch (b.Kind)
            {
                case 0:
                    dy = _random.NextDouble() < 0.82 ? -1 : 0;
                    dx = _random.Next(-1, 2);
                    if (Math.Abs(b.X + dx - Cols / 2) > 3) dx = 0; // the trunk stays above the pot
                    if (b.Age > 4 && b.Age % 2 == 0) _growing.Add(new Branch { X = b.X, Y = b.Y, Life = 5 + _random.Next(9), Kind = b.Age / 2 % 2 == 0 ? 1 : 2 }); // shoots alternate left and right
                    break;
                default:
                    dx = (b.Kind == 1 ? -1 : 1) * (_random.NextDouble() < 0.8 ? 1 : 0);
                    dy = _random.NextDouble() < 0.35 ? -1 : 0;
                    break;
            }
            int x = Math.Clamp(b.X + dx, 1, Cols - 2), y = Math.Clamp(b.Y + dy, 1, Ground);
            char ch = dy == 0 ? (dx == 0 ? '|' : _random.Next(3) == 0 ? '~' : '_') : dx < 0 ? '\\' : dx > 0 ? '/' : '|';
            if (_wood[y, x] == ' ') _wood[y, x] = ch;
            b.X = x;
            b.Y = y;
            if (--b.Life > 0) continue;
            _growing.Remove(b);
            Foliage(x, y, b.Kind == 0 ? 4 : 3);
        }
    }

    private void Foliage(int x, int y, int radius)
    {
        for (int r = y - radius; r <= y + 1; r++)
            for (int c = x - radius * 2; c <= x + radius * 2; c++)
            {
                if (r < 0 || r >= Ground || c < 0 || c >= Cols) continue;
                double d = Math.Pow((c - x) / 2.0, 2) + Math.Pow(r - y, 2);
                if (d <= radius * radius && _random.NextDouble() < 0.85 - d / (radius * radius * 2.5)) _leaves[r, c] = '&';
            }
    }

    // ---------- care ----------

    private double Moisture => Math.Clamp(1 - (DateTime.Now - Settings.Current.BonsaiWatered).TotalHours / 48, 0, 1);

    private void Water()
    {
        Settings.Current.BonsaiWatered = DateTime.Now;
        Settings.Current.Save();
        _rainFrames = 16;
        ShowStats();
    }

    private void Prune()
    {
        // Cuts the outer leaves; they grow back over the next minute.
        for (int r = 0; r < Ground; r++)
            for (int c = 0; c < Cols; c++)
                if (_leaves[r, c] == '&' && (c == 0 || c == Cols - 1 || _leaves[r, c - 1] == ' ' || _leaves[r, c + 1] == ' ') && _random.NextDouble() < 0.6)
                    _leaves[r, c] = ' ';
        _regrow = 60;
        Draw();
    }

    private void Feed()
    {
        // A fed tree pushes a few new shoots.
        for (int i = 0; i < 3; i++)
        {
            int x = Cols / 2 + _random.Next(-4, 5), y = Ground - 6 - _random.Next(8);
            _growing.Add(new Branch { X = x, Y = y, Life = 4 + _random.Next(5), Kind = _random.Next(2) == 0 ? 1 : 2 });
        }
    }

    private void Replant()
    {
        var s = Settings.Current;
        s.BonsaiSeed = new Random().Next(0x1000, 0xFFFF);
        s.BonsaiPlanted = DateTime.Now;
        s.Save();
        Plant(grow: true);
    }

    // ---------- animation ----------

    private int _rainFrames, _regrow;

    private void Tick()
    {
        _frame++;
        bool changed = false;
        if (_growing.Count > 0) { Step(); Step(); changed = true; }
        if (_regrow > 0 && --_regrow % 6 == 0)
        {
            // Pruned leaves come back one by one, next to existing ones.
            for (int tries = 0; tries < 40; tries++)
            {
                int r = _random.Next(Ground), c = _random.Next(1, Cols - 1);
                if (_leaves[r, c] == ' ' && (_leaves[r, c - 1] == '&' || _leaves[r, c + 1] == '&')) { _leaves[r, c] = '&'; break; }
            }
            changed = true;
        }
        if (_rainFrames > 0) { _rainFrames--; changed = true; }
        // Wind: once a second the crown leans the other way.
        if (Settings.Current.BonsaiWind && _frame % 8 == 0) { _swayLeft = !_swayLeft; changed = true; }
        if (_frame % 240 == 0) ShowStats();
        if (changed) Draw();
    }

    protected override void OnResume() => Draw();

    private void Draw()
    {
        var s = Settings.Current;
        var leaves = new char[Rows, Cols];
        Ascii.Clear(leaves);
        char leaf = s.BonsaiWinter ? '*' : '&';
        for (int r = 0; r < Ground; r++)
            for (int c = 0; c < Cols; c++)
            {
                if (_leaves[r, c] != '&') continue;
                // Only the upper crown moves in the wind, the lower part stays put.
                int shift = s.BonsaiWind && r < Ground - 8 && (r + c) % 3 == 0 ? (_swayLeft ? -1 : 1) : 0;
                int to = Math.Clamp(c + shift, 0, Cols - 1);
                leaves[r, to] = leaf;
            }
        if (_rainFrames > 0)
            for (int i = 0; i < 18; i++)
            {
                int r = _random.Next(Ground - 2), c = _random.Next(Cols / 2 - 12, Cols / 2 + 12);
                if (leaves[r, c] == ' ') leaves[r, c] = '\'';
            }
        _woodText.Text = Ascii.Join(_wood);
        _leafText.Text = Ascii.Join(leaves);
    }

    private void ShowStats()
    {
        var s = Settings.Current;
        double moisture = Moisture, health = Math.Clamp(0.35 + moisture * 0.65, 0, 1), growth = _growing.Count > 0 ? 0.5 : 1;
        _stats.Text =
            $"FEUCHTE {Ascii.Gauge(moisture)} {moisture * 100,3:0}%   ZEN    {Ascii.Gauge(s.BonsaiWind ? 0.9 : 1)} {(s.BonsaiWind ? 90 : 100),3}%\n" +
            $"GESUND  {Ascii.Gauge(health)} {health * 100,3:0}%   WUCHS  {Ascii.Gauge(growth)} {growth * 100,3:0}%";
        _stats.LineHeight = 13;
    }

    protected override Point DefaultPlace(Rect w) => new(w.Left + 388, w.Top + 44);
}
