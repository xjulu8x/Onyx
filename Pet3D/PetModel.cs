using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace DesktopOverlay;

/// <summary>What the face shows right now: lids, mouth, mood and where the eyes look (in the model's own left/right).</summary>
internal readonly record struct FaceState(bool Closed, bool Half, bool Open, int Mood, int LookX, int LookY);

/// <summary>
/// The 3D character, described as shapes from the same settings as before (body, ears, eyes, mouth, pattern, hat,
/// accessory, colors) plus proportions. World units match the old pixel grid: x is 0 in the middle, y is 0 on the
/// floor and grows upward, z points to the viewer.
/// </summary>
internal sealed class PetLook
{
    public readonly Settings S;
    public readonly int Shape;
    public readonly double Rx, Ry, Rz, E, Cy, Bottom, Top, EyeY, Ex, K, EarK, Head;
    public readonly Color Body, Second, Eye, Shade, Pink = Color.FromRgb(0xFF, 0x8F, 0xA3);
    private static readonly Color Cool = Color.FromRgb(0x10, 0x14, 0x2A), Warm = Color.FromRgb(0xFF, 0xF6, 0xE0);
    public static readonly Color Gold = Color.FromRgb(0xFF, 0xC9, 0x40), Dark = Color.FromRgb(0x2E, 0x2E, 0x36), Red = Color.FromRgb(0xFF, 0x45, 0x3A);

    public PetLook(Settings s)
    {
        S = s;
        Shape = Math.Clamp(s.PetShape, 0, PixelPet.Shapes.Length - 1);
        (Rx, Ry, Rz, E, Cy) = Shape switch
        {
            1 => (10.4, 9.8, 9.0, 0.42, 12.4),
            2 => (9.0, 12.2, 8.4, 1.0, 14.4),
            3 => (10.6, 10.4, 9.2, 1.0, 13.4),
            4 => (12.0, 8.8, 9.6, 1.0, 11.4),
            _ => (11.0, 10.0, 9.4, 1.0, 12.6),
        };
        Bottom = Shape == 3 ? 1.0 : Cy - Ry;
        Top = Cy + Ry;
        EyeY = Cy + Ry * 0.2;
        Ex = Rx * 0.43;
        K = Math.Clamp(s.PetEyeSize, 0.6, 1.5);
        EarK = Math.Clamp(s.PetEarSize, 0.5, 1.6);
        Head = Math.Clamp(s.PetHead, 0.8, 1.25);
        Body = Settings.ToColor(s.PetColor, 0xFF, Colors.White);
        Second = Settings.ToColor(s.PetColor2, 0xFF, Colors.Orange);
        Eye = Settings.ToColor(s.PetEyeColor, 0xFF, Colors.Black);
        Shade = Mix(Body, Cool, 0.2);
    }

    public static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public Color Lighter(Color c, double t) => Mix(c, Warm, t);
    public Color Darker(Color c, double t) => Mix(c, Cool, t);

    private static double Sq(double v) => v * v;

    /// <summary>Point on the front of the body at (x, y), its normal, lifted off the surface by <paramref name="lift"/>.</summary>
    public (Point3D P, Vector3D N) Surf(double x, double y, double lift = 0)
    {
        double dy = y - Cy;
        Point3D p; Vector3D n;
        if (Shape == 3 && dy < 0)
        {
            double t = Math.Max(0.02, 1 - Sq(x / Rx));
            double z = Rz * Math.Sqrt(t);
            p = new Point3D(x, y, z);
            n = new Vector3D(x / (Rx * Rx), 0, z / (Rz * Rz));
        }
        else
        {
            double e2 = 2 / E;
            double t = Math.Max(0.002, 1 - Math.Pow(Math.Abs(x / Rx), e2) - Math.Pow(Math.Abs(dy / Ry), e2));
            double z = Rz * Math.Pow(t, E / 2);
            p = new Point3D(x, y, z);
            double G(double v, double r) => e2 * Math.Pow(Math.Abs(v / r), e2 - 1) * Math.Sign(v) / r;
            n = new Vector3D(G(x, Rx), G(dy, Ry), G(z, Rz));
        }
        n = Solid.Norm(n);
        return (p + n * lift, n);
    }

    /// <summary>Half width and depth of the body at height y (for scarves and collars).</summary>
    public (double X, double Z) Width(double y)
    {
        double dy = y - Cy;
        if (Shape == 3 && dy < 0) return (Rx * 1.04, Rz * 1.04);
        double t = Math.Max(0, 1 - Math.Pow(Math.Abs(dy / Ry), 2 / E));
        double f = Math.Pow(t, E / 2);
        return (Rx * f, Rz * f);
    }

    /// <summary>Highest point of the head with ears and hat, in rows of the old grid (for the layout).</summary>
    public int Rows()
    {
        double top = Top;
        foreach (var s in Ears().Concat(Hat())) { var b = s.Bounds(); if (!b.IsEmpty) top = Math.Max(top, b.Y + b.SizeY); }
        top = Bottom + (top - Bottom) * Head;
        return (int)Math.Ceiling(top) + 2;
    }

    // ---------- body ----------

    public Solid BodySolid(int wave)
    {
        Solid s = Shape == 3
            ? new Solid { Kind = Solid.Shape.Ghost, R = new Vector3D(Rx, Ry, Rz), Hem = Cy - 1.0, WavePhase = wave * Math.PI, M = Mat.At(new Point3D(0, Cy, 0)) }
            : Solid.Blob(Mat.At(new Point3D(0, Cy, 0)), Rx, Ry, Rz, Body, E);
        s.Color = Body;
        s.Gloss = 0.55;
        s.Outline = true;
        s.Paint = Pattern();
        return s;
    }

    private Func<Point3D, Color> Pattern()
    {
        var c = new Point3D(0, Cy, 0);
        Vector3D Dir(Point3D p) => Solid.Norm(new Vector3D((p.X - c.X) / Rx, (p.Y - c.Y) / Ry, (p.Z - c.Z) / Rz));
        switch (S.PetPattern)
        {
            case 1: // belly
                return p => p.Z > 0 && Sq(p.X / (Rx * 0.6)) + Sq((p.Y - (Cy - Ry * 0.34)) / (Ry * 0.55)) <= 1 ? Second : Body;
            case 2: // stripes over the top and back, the face stays free
                return p => { var d = Dir(p); return d.Z < 0.45 && Math.Sin(p.Y * 1.5 + Math.Abs(p.X) * 0.12) > 0.42 && d.Y > -0.55 ? Second : Body; };
            case 3: // spots
            {
                var spots = new[] { new Vector3D(0.75, 0.45, 0.45), new Vector3D(-0.8, -0.05, 0.55), new Vector3D(-0.75, 0.6, -0.2), new Vector3D(0.3, -0.4, -0.85), new Vector3D(0.9, -0.25, -0.3), new Vector3D(-0.3, 0.85, -0.4), new Vector3D(-0.45, -0.45, -0.75), new Vector3D(0.25, 0.92, 0.2) }.Select(Solid.Norm).ToArray();
                return p => { var d = Dir(p); return spots.Any(sp => Vector3D.DotProduct(d, sp) > 0.93) ? Second : Body; };
            }
            case 4: // two colors: the lower half
                return p => p.Y < Cy - Ry * 0.12 + Math.Sin(Math.Atan2(p.Z, p.X) * 3) * 0.6 ? Second : Body;
            case 5: // little stars
            {
                var dirs = new List<Vector3D>();
                for (int i = 0; i < 26; i++)
                {
                    double y = 1 - (i + 0.5) / 26 * 2, r = Math.Sqrt(1 - y * y), a = i * 2.39996;
                    var d = new Vector3D(Math.Cos(a) * r, y, Math.Sin(a) * r);
                    if (d.Z < 0.55 || d.Y < -0.3) dirs.Add(d);
                }
                return p =>
                {
                    var d = Dir(p);
                    foreach (var sd in dirs)
                    {
                        if (Vector3D.DotProduct(d, sd) < 0.97) continue;
                        var t1 = Solid.Norm(Vector3D.CrossProduct(sd, new Vector3D(0, 1, 0.01)));
                        var t2 = Vector3D.CrossProduct(sd, t1);
                        var diff = d - sd;
                        double a = Math.Abs(Vector3D.DotProduct(diff, t1)), b = Math.Abs(Vector3D.DotProduct(diff, t2));
                        if (Math.Sqrt(a) + Math.Sqrt(b) < 0.36) return Second;
                    }
                    return Body;
                };
            }
            case 6: // mask: a light face around the eyes and down the front, like a penguin
                return p =>
                {
                    if (p.Z <= 0) return Body;
                    var d = Dir(p);
                    double ex = Math.Abs(p.X) - Ex;
                    bool eyes = Sq(ex / (3.7 * K)) + Sq((p.Y - EyeY) / (3.9 * K)) <= 1;
                    bool front = d.Z > 0.3 && p.Y < EyeY - 0.6 && Math.Abs(p.X) < Rx * 0.74;
                    return eyes || front ? Second : Body;
                };
        }
        return null;
    }

    public List<Solid> BodyExtras()
    {
        var list = new List<Solid>();
        double neck = EyeY - 7.4;
        var (wx, wz) = Width(neck);
        IEnumerable<Point3D> Ring(double y, double grow, int n = 28)
        {
            for (int i = 0; i < n; i++) { double a = i * 2 * Math.PI / n; yield return new Point3D(Math.Cos(a) * (wx + grow), y, Math.Sin(a) * (wz + grow)); }
        }
        switch (S.PetAccessory)
        {
            case 1: // glasses: two rings around the eyes and a bridge
            case 2: // sunglasses: two dark shiny lenses
                foreach (int side in new[] { -1, 1 })
                {
                    var (p, n) = Surf(side * Ex, EyeY, 1.2 * K);
                    if (S.PetAccessory == 1)
                    {
                        var ring = Enumerable.Range(0, 20).Select(i => { double a = i * Math.PI / 10; return new Point3D(Math.Cos(a) * 3.0 * K, Math.Sin(a) * 2.9 * K, 0); }).ToArray();
                        var frame = Solid.Tube(ring, 0.38, Dark, closed: true);
                        frame.M = Mat.Facing(p, n);
                        frame.Gloss = 1;
                        list.Add(frame);
                    }
                    else
                    {
                        var lens = Solid.Blob(Mat.Facing(p, n), 3.0 * K, 2.3 * K, 0.5, Color.FromRgb(0x16, 0x16, 0x1E));
                        lens.Gloss = 1.4;
                        list.Add(lens);
                    }
                }
                var bridge = Enumerable.Range(0, 7).Select(i => { double x = -Ex + 3 * K + i / 6.0 * (2 * Ex - 6 * K); return Surf(x, EyeY + 0.9 * K, 1.3 * K).P; });
                list.Add(Solid.Tube(bridge, 0.34, Dark));
                break;
            case 3: // scarf with a hanging end
            {
                var scarf = Solid.Tube(Ring(neck, 0.5), 1.5, Second, closed: true);
                scarf.Gloss = 0.2;
                list.Add(scarf);
                var (p, n) = Surf(Rx * 0.36, neck - 2.6, 0.8);
                var tail = Solid.Blob(Mat.Facing(p, n, -8), 1.7, 3.1, 0.9, Lighter(Second, 0.12));
                tail.Gloss = 0.2;
                list.Add(tail);
                break;
            }
            case 4: // bow tie
            {
                var (p, n) = Surf(0, neck + 0.4, 0.5);
                foreach (int side in new[] { -1, 1 })
                    list.Add(Solid.Lathe(Mat.At(new Point3D(0, 0, 0), rotZ: side * 90, sz: 0.6).Then(Mat.Facing(p, n)), Red, new Point(0, 0), new Point(1.7, 0), new Point(0, 2.6)).Shiny(0.7));
                list.Add(Solid.Blob(Mat.Facing(p + n * 0.3, n), 0.8, 0.75, 0.6, Darker(Red, 0.2)));
                break;
            }
            case 5: // collar with a bell
            {
                list.Add(Solid.Tube(Ring(neck, 0.25), 0.6, Red, closed: true));
                var (p, n) = Surf(0, neck - 1.2, 1.1);
                list.Add(Solid.Blob(Mat.Facing(p, n), 1.55, 1.55, 1.4, Gold).Shiny(1.3));
                list.Add(Solid.Blob(Mat.Facing(p + n * 1.2 + new Vector3D(0, -0.4, 0), n), 0.6, 0.25, 0.3, Color.FromRgb(0x6A, 0x4A, 0x10)));
                break;
            }
        }
        return list;
    }

    // ---------- arms and feet ----------

    public Point3D Shoulder(int side) { double y = Cy - Ry * 0.12; return new Point3D(side * (Width(y).X - 0.8), y, 0.8); }

    public Solid Arm(int side)
    {
        var s = Shoulder(side);
        var arm = Solid.Blob(Mat.At(new Point3D(side * 0.9, -2.4, 0), rotZ: side * 14).Then(Mat.At(s)), 1.75, 3.0, 1.75, Body);
        arm.Outline = true;
        arm.Gloss = 0.45;
        return arm;
    }

    public Point3D FootAt(int side) => new(side * Rx * 0.42, 1.25, 1.4);

    public Solid Foot(int side)
    {
        if (Shape == 3) return null; // ghosts float
        var color = S.PetMouth == 6 ? Color.FromRgb(0xFF, 0x8A, 0x2A) : Darker(Body, 0.12);
        var foot = Solid.Blob(Mat.At(FootAt(side)), 3.0, 1.45, 3.4, color);
        foot.Outline = true;
        foot.Gloss = 0.4;
        return foot;
    }

    // ---------- ears ----------

    public Point3D EarPivot => new(0, Top - 1, 0);

    public List<Solid> Ears()
    {
        var list = new List<Solid>();
        double k = EarK;
        // The base of an ear on the top of the head, tilted with the head's curve.
        (Point3D P, double Tilt) Base(double x, double back = 0)
        {
            double t = Math.Max(0, 1 - Math.Pow(Math.Abs(x / Rx), 2 / E));
            double y = Cy + Ry * Math.Pow(t, E / 2);
            double slope = -Math.Atan2(x / (Rx * Rx), Math.Max(0.02, (y - Cy) / (Ry * Ry))) * 180 / Math.PI;
            return (new Point3D(x, y - 0.9, back), slope);
        }
        switch (S.PetEars)
        {
            case 1: // cat
            case 6: // fox
                foreach (int side in new[] { -1, 1 })
                {
                    bool fox = S.PetEars == 6;
                    var (p, tilt) = Base(side * Rx * (fox ? 0.5 : 0.52), -1);
                    double r = (fox ? 3.6 : 3.0) * k, h = (fox ? 7.0 : 5.2) * k;
                    var at = Mat.At(new Point3D(0, 0, 0), rotZ: tilt * 0.8 + side * -(fox ? 12 : 6), sz: 0.55).Then(Mat.At(p));
                    list.Add(new Solid { Kind = Solid.Shape.Lathe, Profile = new[] { new Point(0, 0), new Point(r, 0), new Point(0.25, h * 0.92), new Point(0, h) }, Color = Body, M = at, Outline = true, Gloss = 0.45 });
                    var inner = Mat.At(new Point3D(0, 0.4, 0), sz: 0.4).Then(Mat.At(new Point3D(0, 0, 1.1 * k))).Then(Mat.At(new Point3D(0, 0, 0), rotZ: tilt * 0.8 + side * -(fox ? 12 : 6))).Then(Mat.At(p));
                    list.Add(new Solid { Kind = Solid.Shape.Lathe, Profile = new[] { new Point(0, 0), new Point(r * 0.58, 0), new Point(0, h * 0.72) }, Color = fox ? Color.FromRgb(0xF2, 0xE3, 0xC6) : Pink, M = inner, Gloss = 0.3, Decal = true });
                }
                break;
            case 2: // bunny
                foreach (int side in new[] { -1, 1 })
                {
                    var (p, tilt) = Base(side * Rx * 0.3);
                    var at = Mat.At(new Point3D(0, 5.2 * k, 0), rotZ: tilt * 0.5 - side * 8).Then(Mat.At(p));
                    list.Add(Solid.Blob(at, 1.8 * k, 5.9 * k, 1.1 * k, Body).Outlined());
                    var inner = Mat.At(new Point3D(0, 5.4 * k, 0.75 * k), rotZ: tilt * 0.5 - side * 8).Then(Mat.At(p));
                    list.Add(Solid.Blob(inner, 1.0 * k, 4.6 * k, 0.55 * k, Pink).Decaled());
                }
                break;
            case 3: // bear
                foreach (int side in new[] { -1, 1 })
                {
                    var (p, tilt) = Base(side * Rx * 0.64);
                    var at = Mat.At(new Point3D(0, 1.6 * k, 0), rotZ: tilt * 0.6).Then(Mat.At(p));
                    list.Add(Solid.Blob(at, 2.7 * k, 2.6 * k, 1.5 * k, Body).Outlined());
                    var inner = Mat.At(new Point3D(0, 1.5 * k, 1.0 * k), rotZ: tilt * 0.6).Then(Mat.At(p));
                    list.Add(Solid.Blob(inner, 1.55 * k, 1.45 * k, 0.75 * k, Darker(Second, 0.15)).Decaled());
                }
                break;
            case 4: // antenna with a glowing ball
            {
                var (p, _) = Base(0);
                var path = Enumerable.Range(0, 10).Select(i => { double t = i / 9.0; return p + new Vector3D(Math.Sin(t * 1.4) * 1.9 * k, t * 4.8 * k, 0); }).ToArray();
                list.Add(Solid.Tube(path, 0.38, Dark));
                list.Add(Solid.Blob(Mat.At(path[^1] + new Vector3D(0, 0.5, 0)), 1.15 * k, 1.15 * k, 1.15 * k, Second).Glowing(0.55));
                break;
            }
            case 5: // horns: tubes that curve outward and get thin
                foreach (int side in new[] { -1, 1 })
                {
                    var (p, _) = Base(side * Rx * 0.45);
                    var path = Enumerable.Range(0, 12).Select(i => { double t = i / 11.0; return p + new Vector3D(side * (t * t * 3.4) * k, (Math.Sin(t * 1.9) * 4.0) * k, -t * 0.6); }).ToArray();
                    list.Add(new Solid { Kind = Solid.Shape.Tube, Path = path, Radius = t => (1.45 - t * 1.25) * k, Color = Color.FromRgb(0xF2, 0xE3, 0xC6), Gloss = 0.9, Outline = true });
                }
                break;
        }
        return list;
    }

    // ---------- hats ----------

    public Point3D HatPivot => new(0, Top, 0);

    public List<Solid> Hat()
    {
        var list = new List<Solid>();
        double top = Top;
        switch (S.PetHat)
        {
            case 1: // cap
                list.Add(Solid.Blob(Mat.At(new Point3D(0, top - 1.6, -0.3)), Rx * 0.78, 4.2, Rz * 0.8, Second).Outlined());
                list.Add(Solid.Blob(Mat.At(new Point3D(0, top - 1.2, Rz * 0.55), rotX: 12), 5.3, 0.55, 4.4, Darker(Second, 0.12)).Outlined());
                list.Add(Solid.Blob(Mat.At(new Point3D(0, top + 2.6, -0.3)), 0.85, 0.6, 0.85, Lighter(Second, 0.3)));
                break;
            case 2: // crown
                list.Add(Solid.Lathe(Mat.At(new Point3D(0, top - 1.3, 0)), Gold, new Point(0, 0), new Point(5.2, 0), new Point(5.2, 2.1), new Point(0, 2.1)).Shiny(1.3));
                for (int i = 0; i < 5; i++)
                {
                    double a = (90 + (i - 2) * 38) * Math.PI / 180;
                    var at = new Point3D(Math.Cos(a) * 4.7, top + 0.7, Math.Sin(a) * 4.7);
                    list.Add(Solid.Lathe(Mat.At(at), Gold, new Point(0, 0), new Point(1.0, 0), new Point(0, 2.7)).Shiny(1.3));
                    list.Add(Solid.Blob(Mat.At(at + new Vector3D(0, 2.8, 0)), 0.5, 0.5, 0.5, Gold).Shiny(1.3));
                    if (i % 2 == 0) list.Add(Solid.Blob(Mat.At(new Point3D(Math.Cos(a) * 5.3, top - 0.25, Math.Sin(a) * 5.3)), 0.75, 0.75, 0.45, i == 2 ? Red : Color.FromRgb(0x4F, 0xD1, 0xC5)).Shiny(1.5));
                }
                break;
            case 3: // headphones
            {
                double y = EyeY + 0.6, wx = Width(y).X + 0.6;
                var band = Enumerable.Range(0, 18).Select(i => { double a = i * Math.PI / 17; return new Point3D(Math.Cos(a) * wx, y + Math.Sin(a) * (top - y + 1.2), -0.6); });
                list.Add(Solid.Tube(band, 0.7, Dark).Shiny(0.8));
                foreach (int side in new[] { -1, 1 })
                {
                    var at = Mat.At(new Point3D(0, 0, 0), rotZ: side * 90).Then(Mat.At(new Point3D(side * (wx - 0.6), y, 0)));
                    list.Add(Solid.Lathe(at, Second, new Point(0, -0.2), new Point(2.5, -0.2), new Point(2.5, 1.6), new Point(0, 1.6)).Shiny(0.9));
                    list.Add(Solid.Lathe(at, Dark, new Point(0, -0.7), new Point(2.0, -0.7), new Point(2.0, -0.1), new Point(0, -0.1)));
                }
                break;
            }
            case 4: // beanie with a pom-pom
                list.Add(Solid.Blob(Mat.At(new Point3D(0, top - 1.9, 0)), Rx * 0.82, 5.4, Rz * 0.86, Second).Outlined().Matte());
                list.Add(Solid.Tube(Enumerable.Range(0, 28).Select(i => { double a = i * 2 * Math.PI / 28; var w = Width(top - 2.6); return new Point3D(Math.Cos(a) * (w.X + 0.5), top - 2.6, Math.Sin(a) * (w.Z + 0.5)); }), 1.1, Lighter(Second, 0.25), closed: true).Matte());
                list.Add(Solid.Blob(Mat.At(new Point3D(0, top + 3.7, 0)), 1.9, 1.8, 1.9, Color.FromRgb(0xFA, 0xF7, 0xF0)).Matte());
                break;
            case 5: // halo
            {
                var ring = Enumerable.Range(0, 30).Select(i => { double a = i * 2 * Math.PI / 30; return new Point3D(Math.Cos(a) * 5.6, 0, Math.Sin(a) * 4.8); }).ToArray();
                var halo = Solid.Tube(ring, 0.6, Gold, closed: true);
                halo.M = Mat.At(new Point3D(0, top + 3.4, 0), rotX: 14);
                halo.Glow = 0.75;
                list.Add(halo);
                break;
            }
            case 6: // flower
            {
                var (p, n) = Surf(Rx * 0.42, top - 2.6, 0.2);
                var face = Mat.Facing(p, n + new Vector3D(0, 0.9, 0));
                for (int i = 0; i < 5; i++)
                {
                    double a = i * 72 + 90;
                    var petal = Mat.At(new Point3D(1.65, 0, 0)).Then(Mat.At(new Point3D(0, 0, 0), rotZ: a)).Then(face);
                    list.Add(Solid.Blob(petal, 1.6, 1.0, 0.5, Second));
                }
                list.Add(Solid.Blob(Mat.At(new Point3D(0, 0, 0.35)).Then(face), 1.05, 1.05, 0.7, Color.FromRgb(0xFF, 0xD6, 0x0A)));
                break;
            }
            case 7: // bow
            {
                var (p, n) = Surf(-Rx * 0.38, top - 1.4, 0.5);
                var face = Mat.Facing(p, n + new Vector3D(0, 0.5, 0), 12);
                foreach (int side in new[] { -1, 1 })
                    list.Add(Solid.Blob(Mat.At(new Point3D(side * 2.0, 0, 0), rotZ: side * 14).Then(face), 2.2, 1.5, 0.75, Second).Outlined());
                list.Add(Solid.Blob(Mat.At(new Point3D(0, 0, 0.3)).Then(face), 0.95, 0.95, 0.85, Darker(Second, 0.15)));
                break;
            }
            case 8: // wizard hat: a cone that bends at the tip
            {
                var purple = Color.FromRgb(0x8E, 0x5C, 0xFF);
                var path = Enumerable.Range(0, 16).Select(i => { double t = i / 15.0; return new Point3D(t * t * 3.0, top - 1.4 + t * 10.5, -t * t * 0.8); }).ToArray();
                list.Add(new Solid { Kind = Solid.Shape.Tube, Path = path, Radius = t => 6.0 * Math.Pow(1 - t, 1.15) + 0.3, Color = purple, Gloss = 0.5, Outline = true });
                list.Add(Solid.Blob(Mat.At(new Point3D(0, top - 1.5, 0)), 9.2, 0.6, 7.8, Darker(purple, 0.15)).Outlined());
                list.Add(Solid.Tube(Enumerable.Range(0, 28).Select(i => { double a = i * 2 * Math.PI / 28; return new Point3D(Math.Cos(a) * 5.7, top - 0.7, Math.Sin(a) * 5.7); }), 0.5, Gold, closed: true).Shiny(1.2));
                list.Add(Solid.Extrude(Mat.At(new Point3D(0.6, top + 3.2, 4.2), rotX: -18), Solid.Star(5, 1.3, 0.55), 0.5, Color.FromRgb(0xFF, 0xD6, 0x0A)).Glowing(0.3));
                break;
            }
        }
        return list;
    }

    // ---------- face ----------

    public List<Solid> Face(FaceState f)
    {
        var list = new List<Solid>();
        int mood = f.Mood;
        double k = K, ey = EyeY;
        double lx = f.LookX * 0.75 * k, ly = -f.LookY * 0.6 * k;
        bool halfLids = f.Half || mood is PixelPet.Bored or PixelPet.Sleepy or PixelPet.Hot;
        var ink = Eye;
        double line = 0.42 * Math.Min(1.2, k);

        Solid Add(Solid s) { s.Decal = true; list.Add(s); return s; }
        // A line drawn on the face along y = y0 + curve·(x - x0)².
        void Arc(double x0, double y0, double half, double curve, Color c, double r, double lift = 0.15, bool glow = false)
        {
            var pts = Enumerable.Range(0, 13).Select(i => { double x = x0 - half + i / 12.0 * 2 * half; return Surf(x, y0 + curve * Sq(x - x0), lift).P; });
            var s = Add(Solid.Tube(pts, r, c));
            s.Gloss = 0.4;
            if (glow) s.Glow = 0.9;
        }
        Solid Glint(Point3D at, double r) { var s = Add(Solid.Blob(Mat.At(at), r, r, r, Colors.White)); s.Glow = 1; s.Gloss = 0; return s; }

        if (S.PetEyes == 4)
        {
            // Visor: a glossy band across the face that shows the eyes as a small display.
            var band = new List<Solid>();
            double x0 = -Rx * 0.8, x1 = Rx * 0.8, y0 = ey - 2.2 * k, y1 = ey + 2.0 * k;
            var rows = new List<Point3D>();
            var visor = new Solid { Kind = Solid.Shape.Tube };
            _ = visor;
            for (int i = 0; i < 9; i++)
            {
                double y = y0 + (y1 - y0) * i / 8.0;
                var pts = Enumerable.Range(0, 17).Select(j => Surf(x0 + (x1 - x0) * j / 16.0, y, 0.55).P).ToArray();
                var strip = Add(Solid.Tube(pts, (y1 - y0) / 14, Second));
                strip.Gloss = 1.3;
                strip.Glow = 0.08;
            }
            foreach (int side in new[] { -1, 1 })
            {
                double ex = side * Ex;
                var white = Color.FromRgb(0xF4, 0xFF, 0xFC);
                if (f.Closed) Arc(ex, ey - 0.2, 1.8 * k, 0, white, 0.3, 1.0, true);
                else if (mood == PixelPet.Happy) Arc(ex, ey + 0.3, 1.6 * k, -0.3 / k, white, 0.32, 1.0, true);
                else if (mood == PixelPet.Love) { var (p, n) = Surf(ex, ey, 1.0); Add(Solid.Extrude(Mat.Facing(p, n), Solid.Heart(1.6 * k), 0.3, Color.FromRgb(0xFF, 0x6B, 0x8A))).Glow = 0.9; }
                else if (mood == PixelPet.Surprised) { var (p, n) = Surf(ex, ey, 1.0); var ring = Enumerable.Range(0, 16).Select(i => { double a = i * Math.PI / 8; return new Point3D(Math.Cos(a) * 1.1 * k, Math.Sin(a) * 1.1 * k, 0); }); var r = Add(Solid.Tube(ring, 0.3, white, closed: true)); r.M = Mat.Facing(p, n); r.Glow = 0.9; }
                else if (halfLids) Arc(ex + lx * 0.5, ey, 1.0 * k, 0, white, 0.32, 1.0, true);
                else { var (p, n) = Surf(ex + lx * 0.8, ey + ly * 0.5, 0.95); Add(Solid.Blob(Mat.Facing(p, n), 0.85 * k, 1.05 * k, 0.3, white)).Glow = 0.95; }
            }
            var shine = Surf(-Rx * 0.42 + f.LookX * 1.2, ey + 1.3 * k, 1.05);
            Add(Solid.Blob(Mat.Facing(shine.P, shine.N), 2.4, 0.3, 0.15, Colors.White)).Glow = 0.5;
        }
        else
        {
            foreach (int side in new[] { -1, 1 })
            {
                double ex = side * Ex;
                if (f.Closed) { Arc(ex, ey - 0.8 * k, 2.0 * k, 0.12 / k, ink, line); continue; }
                if (mood == PixelPet.Happy) { Arc(ex, ey + 0.4 * k, 2.0 * k, -0.24 / k, ink, line); continue; }
                if (mood == PixelPet.Love)
                {
                    var (p, n) = Surf(ex, ey, 0.25);
                    Add(Solid.Extrude(Mat.Facing(p, n), Solid.Heart(2.5 * k), 0.7, Color.FromRgb(0xFF, 0x6B, 0x8A))).Gloss = 1;
                    Glint(Surf(ex - 0.9 * k, ey + 0.8 * k, 0.75).P, 0.45 * k);
                    continue;
                }
                bool wide = mood == PixelPet.Surprised;
                double w = wide ? 1.15 : 1;
                double eyeW, eyeH; Matrix3D frame;
                switch (S.PetEyes)
                {
                    case 0: // dots
                    {
                        eyeW = 1.3 * k * w; eyeH = 1.6 * k * w;
                        var (p, n) = Surf(ex + lx * 0.5, ey + ly * 0.5, 0.05);
                        frame = Mat.Facing(p, n);
                        var dot = Add(Solid.Blob(frame, eyeW, eyeH, 0.7 * k, ink));
                        dot.Gloss = 1.2;
                        Glint(Mat.At(new Point3D(-eyeW * 0.35, eyeH * 0.35, 0.6 * k)).Then(frame).Transform(new Point3D()), 0.4 * k);
                        break;
                    }
                    case 2: // tired: low, half-covered eyes
                    {
                        eyeW = 2.1 * k; eyeH = 1.2 * k;
                        var (p, n) = Surf(ex + lx * 0.4, ey - 0.6 * k, 0.05);
                        frame = Mat.Facing(p, n);
                        Add(Solid.Blob(frame, eyeW, eyeH, 0.6 * k, ink)).Gloss = 1.1;
                        Arc(ex, ey + 0.45 * k, 2.4 * k, 0, Darker(Body, 0.4), line);
                        Glint(Mat.At(new Point3D(-0.6 * k, 0.1, 0.55 * k)).Then(frame).Transform(new Point3D()), 0.32 * k);
                        break;
                    }
                    case 3: // happy arcs
                        Arc(ex, ey + 0.2 * k, 2.1 * k, -0.22 / k, ink, line);
                        continue;
                    case 5: // stars
                    {
                        eyeW = eyeH = 2.4 * k * w;
                        var (p, n) = Surf(ex + lx * 0.4, ey + ly * 0.4, 0.15);
                        frame = Mat.Facing(p, n);
                        var star = Add(Solid.Extrude(frame, Solid.Star(5, eyeW, eyeW * 0.44), 0.7, Color.FromRgb(0xFF, 0xD6, 0x0A)));
                        star.Glow = 0.2; star.Gloss = 1;
                        Glint(Mat.At(new Point3D(-0.5 * k, 0.6 * k, 0.5)).Then(frame).Transform(new Point3D()), 0.35 * k);
                        break;
                    }
                    default: // big eyes: white, colored iris that looks around, pupil and two glints
                    {
                        eyeW = (wide ? 2.6 : 2.3) * k; eyeH = (wide ? 3.0 : 2.7) * k;
                        double depth = 1.1 * k;
                        var (p, n) = Surf(ex, ey, 0);
                        frame = Mat.Facing(p, n);
                        var white = Add(Solid.Blob(frame, eyeW, eyeH, depth, Colors.White));
                        white.Gloss = 1.1; white.Outline = true;
                        double ir = (wide ? 1.1 : 1.65) * k;
                        double ix = Math.Clamp(lx, -eyeW + ir, eyeW - ir), iy = Math.Clamp(ly, -eyeH + ir * 1.2, eyeH - ir * 1.2);
                        double front = depth * Math.Sqrt(Math.Max(0.05, 1 - Sq(ix / eyeW) - Sq(iy / eyeH)));
                        Matrix3D Local(double x, double y, double z) => Mat.At(new Point3D(x, y, z)).Then(frame);
                        Add(Solid.Blob(Local(ix, iy, front - 0.3 * k), ir, ir * 1.15, 0.5 * k, ink)).Gloss = 1;
                        Add(Solid.Blob(Local(ix, iy - ir * 0.5, front - 0.12 * k), ir * 0.72, ir * 0.42, 0.32 * k, Mix(ink, Colors.White, 0.32))).Gloss = 0.8;
                        Add(Solid.Blob(Local(ix, iy + ir * 0.05, front + 0.0), ir * 0.5, ir * 0.58, 0.3 * k, Mix(ink, Colors.Black, 0.7))).Gloss = 1.2;
                        Glint(Local(ix - ir * 0.45, iy + ir * 0.5, front + 0.2 * k).Transform(new Point3D()), 0.55 * k);
                        Glint(Local(ix + ir * 0.45, iy - ir * 0.4, front + 0.15 * k).Transform(new Point3D()), 0.24 * k);
                        break;
                    }
                }
                if (halfLids && S.PetEyes != 2)
                {
                    // A lid in the body color over the upper half: bored, sleepy, hot or half a blink.
                    var lid = Add(Solid.Blob(Mat.At(new Point3D(0, eyeH * 0.62, 0.05)).Then(frame), eyeW * 1.18, eyeH * 0.64, (S.PetEyes == 1 ? 1.25 : 0.95) * k, Darker(Body, 0.1)));
                    lid.Gloss = 0.5;
                    var edge = Mat.At(new Point3D(0, eyeH * 0.0, 0)).Then(frame);
                    _ = edge;
                }
            }
        }

        // Mouth; the mood can change it.
        double my = ey - (S.PetEyes == 4 ? 4.4 : 5.0);
        int mouth = f.Open && S.PetMouth != 0 && S.PetMouth != 6 ? 4 : S.PetMouth;
        if (S.PetMouth != 6)
            mouth = mood switch { PixelPet.Happy => 5, PixelPet.Surprised => 4, PixelPet.Love => 1, PixelPet.Hot => 2, PixelPet.Bored => 7, PixelPet.Sleepy => 8, _ => mouth };
        switch (mouth)
        {
            case 1: case 3: case 5:
                Arc(0, my, 2.3, 0.13, ink, line);
                if (mouth == 3) foreach (int side in new[] { -1, 1 }) { var (p, n) = Surf(side * 1.0, my - 0.7, 0.1); Add(Solid.Blob(Mat.Facing(p, n), 0.45, 0.6, 0.3, Colors.White)).Gloss = 1; }
                if (mouth == 5) { var (p, n) = Surf(0.45, my - 0.95, 0.05); Add(Solid.Blob(Mat.Facing(p, n), 0.95, 1.0, 0.45, Color.FromRgb(0xFF, 0x6B, 0x8A))).Gloss = 1; }
                break;
            case 2: // :3
            {
                var pts = Enumerable.Range(0, 17).Select(i => { double x = -2.4 + i * 0.3; return Surf(x, my - 0.9 + Math.Abs(Math.Sin(x * 1.35)) * 0.8, 0.15).P; });
                Add(Solid.Tube(pts, line, ink)).Gloss = 0.4;
                break;
            }
            case 4: // open
            {
                var (p, n) = Surf(0, my - 0.5, -0.12);
                Add(Solid.Blob(Mat.Facing(p, n), 1.5, 1.65, 0.55, Mix(ink, Colors.Black, 0.3))).Gloss = 0.6;
                var (t, tn) = Surf(0, my - 1.2, 0.08);
                Add(Solid.Blob(Mat.Facing(t, tn), 0.95, 0.7, 0.4, Color.FromRgb(0xFF, 0x6B, 0x8A))).Gloss = 0.9;
                break;
            }
            case 6: // beak
            {
                var (p, n) = Surf(0, ey - 2.7, -0.3);
                var top = Mat.At(new Point3D(0, 0, 0), rotX: 90, sy: 1, sx: 1).Then(Mat.At(new Point3D(0, 0, 0), sy: 0.55)).Then(Mat.Facing(p, n));
                Add(Solid.Lathe(top, Color.FromRgb(0xFF, 0x8A, 0x2A), new Point(0, 0), new Point(2.5, 0), new Point(0, 3.0))).Gloss = 0.9;
                var low = Mat.At(new Point3D(0, 0, 0), rotX: 90).Then(Mat.At(new Point3D(0, -0.9, -0.2), sy: 0.4)).Then(Mat.Facing(p, n));
                Add(Solid.Lathe(low, Color.FromRgb(0xD9, 0x66, 0x1A), new Point(0, 0), new Point(1.8, 0), new Point(0, 2.0))).Gloss = 0.7;
                break;
            }
            case 7: // bored: a flat, slightly crooked line
            {
                var pts = Enumerable.Range(0, 9).Select(i => { double x = -1.8 + i * 0.45; return Surf(x, my + 0.1 - x * 0.08, 0.15).P; });
                Add(Solid.Tube(pts, line, ink));
                break;
            }
            case 8: // sleepy: a small o
            {
                var (p, n) = Surf(0, my - 0.2, 0.1);
                var ring = Enumerable.Range(0, 14).Select(i => { double a = i * 2 * Math.PI / 14; return new Point3D(Math.Cos(a) * 0.75, Math.Sin(a) * 0.85, 0); });
                var o = Add(Solid.Tube(ring, 0.3, ink, closed: true));
                o.M = Mat.Facing(p, n);
                break;
            }
        }

        if (S.PetCheeks)
            foreach (int side in new[] { -1, 1 })
            {
                var (p, n) = Surf(side * (Ex + 2.6 * Math.Min(k, 1.2)), ey - 2.7 * Math.Min(k, 1.2), 0.02);
                var cheek = Add(Solid.Blob(Mat.Facing(p, n), 1.55 * Math.Min(k, 1.2), 0.9, 0.3, Pink));
                cheek.Gloss = 0.15; cheek.Glow = 0.12;
            }
        return list;
    }
}

internal static class SolidExtensions
{
    public static Solid Outlined(this Solid s) { s.Outline = true; return s; }
    public static Solid Decaled(this Solid s) { s.Decal = true; return s; }
    public static Solid Matte(this Solid s) { s.Gloss = 0.1; return s; }
    public static Solid Shiny(this Solid s, double g) { s.Gloss = g; return s; }
    public static Solid Glowing(this Solid s, double g) { s.Glow = g; return s; }
}
