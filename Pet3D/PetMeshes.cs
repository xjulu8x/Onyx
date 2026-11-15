using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace DesktopOverlay;

/// <summary>
/// One shape of the 3D character: a rounded blob, the ghost body, a tube along a path (mouths, horns, rings, hats),
/// a lathe (cones, cylinders) or an extruded outline (stars, hearts). Each can make a smooth mesh and answer whether
/// a point is inside it, so the same shape also becomes cubes in the voxel style.
/// </summary>
internal sealed class Solid
{
    public enum Shape { Blob, Ghost, Tube, Lathe, Extrude }
    public Shape Kind;
    public Vector3D R = new(1, 1, 1);
    public double E = 1, Hem, WavePhase;
    public Point3D[] Path;
    public Func<double, double> Radius = _ => 0.4;
    public bool Closed;
    public Point[] Profile; // lathe: (radius, height), bottom to top
    public Point[] Poly;
    public double Depth = 0.5;
    public Matrix3D M = Matrix3D.Identity;
    public Color Color = Colors.White;
    public Func<Point3D, Color> Paint; // a pattern by position in the part; null = one color
    public double Gloss = 0.6, Glow;
    public bool Outline, Decal;

    private Matrix3D? _inv;
    private Matrix3D Inv { get { if (_inv == null) { var m = M; m.Invert(); _inv = m; } return _inv.Value; } }

    public static Solid Blob(Matrix3D at, double rx, double ry, double rz, Color c, double e = 1) =>
        new() { Kind = Shape.Blob, R = new Vector3D(rx, ry, rz), E = e, Color = c, M = at };

    public static Solid Tube(IEnumerable<Point3D> path, double radius, Color c, bool closed = false) =>
        new() { Kind = Shape.Tube, Path = path.ToArray(), Radius = _ => radius, Color = c, Closed = closed };

    public static Solid Lathe(Matrix3D at, Color c, params Point[] profile) =>
        new() { Kind = Shape.Lathe, Profile = profile, Color = c, M = at };

    public static Solid Extrude(Matrix3D at, Point[] poly, double depth, Color c) =>
        new() { Kind = Shape.Extrude, Poly = poly, Depth = depth, Color = c, M = at };

    // ---------- inside test (for the voxel style) ----------

    public bool Inside(Point3D p, double m)
    {
        var q = Inv.Transform(p);
        switch (Kind)
        {
            case Shape.Blob:
                return Pw(Math.Abs(q.X) / (R.X + m), 2 / E) + Pw(Math.Abs(q.Y) / (R.Y + m), 2 / E) + Pw(Math.Abs(q.Z) / (R.Z + m), 2 / E) <= 1;
            case Shape.Ghost:
                if (q.Y >= 0) return Sq(q.X / (R.X + m)) + Sq(q.Y / (R.Y + m)) + Sq(q.Z / (R.Z + m)) <= 1;
                return q.Y >= -HemAt(Math.Atan2(q.Z, q.X)) - m && Sq(q.X / (R.X * 1.04 + m)) + Sq(q.Z / (R.Z * 1.04 + m)) <= 1;
            case Shape.Tube:
            {
                var (_, total) = Lengths();
                double run = 0;
                int n = Path.Length, segs = Closed ? n : n - 1;
                for (int i = 0; i < segs; i++)
                {
                    var a = Path[i]; var b = Path[(i + 1) % n];
                    var ab = b - a; double len = ab.Length;
                    double t = len < 1e-9 ? 0 : Math.Clamp(Vector3D.DotProduct(q - a, ab) / (len * len), 0, 1);
                    var c = a + ab * t;
                    double r = Radius((run + t * len) / total);
                    if ((q - c).Length <= r + m) return true;
                    run += len;
                }
                return false;
            }
            case Shape.Lathe:
            {
                double radial = Math.Sqrt(q.X * q.X + q.Z * q.Z), best = -1;
                for (int i = 0; i + 1 < Profile.Length; i++)
                {
                    var a = Profile[i]; var b = Profile[i + 1];
                    double lo = Math.Min(a.Y, b.Y), hi = Math.Max(a.Y, b.Y);
                    if (q.Y < lo - m || q.Y > hi + m || hi - lo < 1e-9) continue;
                    double t = Math.Clamp((q.Y - a.Y) / (b.Y - a.Y), 0, 1);
                    best = Math.Max(best, a.X + (b.X - a.X) * t);
                }
                return best >= 0 && radial <= best + m;
            }
            case Shape.Extrude:
                if (Math.Abs(q.Z) > Depth / 2 + m) return false;
                if (InPoly(q.X, q.Y)) return true;
                for (int i = 0; i < Poly.Length; i++)
                    if (SegDist(new Point(q.X, q.Y), Poly[i], Poly[(i + 1) % Poly.Length]) <= m) return true;
                return false;
        }
        return false;
    }

    /// <summary>Box around the shape, in the part's space.</summary>
    public Rect3D Bounds()
    {
        Rect3D local = Kind switch
        {
            Shape.Blob => new Rect3D(-R.X, -R.Y, -R.Z, 2 * R.X, 2 * R.Y, 2 * R.Z),
            Shape.Ghost => new Rect3D(-R.X * 1.05, -Hem - 1.2, -R.Z * 1.05, 2.1 * R.X, R.Y + Hem + 1.2, 2.1 * R.Z),
            Shape.Lathe => LatheBox(),
            Shape.Extrude => PolyBox(),
            _ => Rect3D.Empty,
        };
        if (Kind == Shape.Tube)
        {
            double r = Enumerable.Range(0, 11).Max(i => Radius(i / 10.0));
            var box = Rect3D.Empty;
            foreach (var p in Path) box.Union(new Rect3D(p.X - r, p.Y - r, p.Z - r, 2 * r, 2 * r, 2 * r));
            local = box;
        }
        var result = Rect3D.Empty;
        foreach (var c in Corners(local)) result.Union(M.Transform(c));
        return result;
    }

    private Rect3D LatheBox()
    {
        double r = Profile.Max(p => p.X), lo = Profile.Min(p => p.Y), hi = Profile.Max(p => p.Y);
        return new Rect3D(-r, lo, -r, 2 * r, hi - lo, 2 * r);
    }

    private Rect3D PolyBox()
    {
        double x0 = Poly.Min(p => p.X), x1 = Poly.Max(p => p.X), y0 = Poly.Min(p => p.Y), y1 = Poly.Max(p => p.Y);
        return new Rect3D(x0, y0, -Depth / 2, x1 - x0, y1 - y0, Depth);
    }

    private static IEnumerable<Point3D> Corners(Rect3D b)
    {
        for (int i = 0; i < 8; i++)
            yield return new Point3D(b.X + ((i & 1) != 0 ? b.SizeX : 0), b.Y + ((i & 2) != 0 ? b.SizeY : 0), b.Z + ((i & 4) != 0 ? b.SizeZ : 0));
    }

    public double HemAt(double angle) => Hem + 0.85 * Math.Sin(5 * angle + WavePhase);

    // ---------- smooth mesh (soft style) ----------

    /// <summary>The mesh in the part's space, and an outline shell (inverted hull) if wanted.</summary>
    public (MeshGeometry3D Mesh, MeshGeometry3D Hull) Mesh(int detail, double outline)
    {
        var b = new MeshB();
        switch (Kind)
        {
            case Shape.Blob:
            case Shape.Ghost:
                int nu = Math.Max(10, detail), nv = Math.Max(6, detail / 2 + (Kind == Shape.Ghost ? detail / 3 : 0));
                b.Param(Param, nu, nv);
                break;
            case Shape.Tube: TubeMesh(b, Math.Max(6, detail / 3)); break;
            case Shape.Lathe: LatheMesh(b, Math.Max(8, detail / 2)); break;
            case Shape.Extrude: ExtrudeMesh(b); break;
        }
        b.Apply(M);
        return (b.Done(), outline > 0 ? b.Hull(outline) : null);
    }

    /// <summary>Point on a blob or ghost for u (around, 0..1) and v (bottom to top, 0..1).</summary>
    public Point3D Param(double u, double v)
    {
        double th = u * 2 * Math.PI;
        if (Kind == Shape.Blob)
        {
            double ph = -Math.PI / 2 + v * Math.PI, cp = Math.Cos(ph);
            return new Point3D(R.X * Sp(cp, E) * Sp(Math.Cos(th), E), R.Y * Sp(Math.Sin(ph), E), R.Z * Sp(cp, E) * Sp(Math.Sin(th), E));
        }
        // Ghost: a round top, a straight skirt with a wavy hem, and a slightly raised underside.
        double hem = HemAt(th);
        if (v >= 0.5)
        {
            double ph = (v - 0.5) * Math.PI;
            return new Point3D(R.X * Math.Cos(ph) * Math.Cos(th), R.Y * Math.Sin(ph), R.Z * Math.Cos(ph) * Math.Sin(th));
        }
        if (v >= 0.12)
        {
            double w = (v - 0.12) / 0.38, flare = 1 + 0.04 * (1 - w);
            return new Point3D(R.X * flare * Math.Cos(th), -hem * (1 - w), R.Z * flare * Math.Sin(th));
        }
        double k = v / 0.12;
        double y = -(Hem * 0.82 + (hem - Hem * 0.82) * k * k);
        return new Point3D(R.X * 1.04 * k * Math.Cos(th), y, R.Z * 1.04 * k * Math.Sin(th));
    }

    private (double[] At, double Total) Lengths()
    {
        int n = Path.Length;
        var at = new double[n + 1];
        for (int i = 1; i <= n; i++) at[i] = at[i - 1] + (i < n || Closed ? (Path[i % n] - Path[i - 1]).Length : 0);
        double total = Closed ? at[n] : at[n - 1];
        return (at, Math.Max(1e-6, total));
    }

    private void TubeMesh(MeshB b, int rings)
    {
        int n = Path.Length;
        var (at, total) = Lengths();
        var tangent = new Vector3D[n];
        for (int i = 0; i < n; i++)
        {
            var prev = Closed ? Path[(i - 1 + n) % n] : Path[Math.Max(0, i - 1)];
            var next = Closed ? Path[(i + 1) % n] : Path[Math.Min(n - 1, i + 1)];
            var t = next - prev;
            tangent[i] = t.Length < 1e-9 ? new Vector3D(0, 1, 0) : t / t.Length;
        }
        var normal = new Vector3D[n];
        var seed = Math.Abs(tangent[0].Y) < 0.9 ? new Vector3D(0, 1, 0) : new Vector3D(1, 0, 0);
        normal[0] = Norm(seed - tangent[0] * Vector3D.DotProduct(seed, tangent[0]));
        for (int i = 1; i < n; i++)
        {
            var nn = normal[i - 1] - tangent[i] * Vector3D.DotProduct(normal[i - 1], tangent[i]);
            normal[i] = nn.Length < 1e-9 ? normal[i - 1] : Norm(nn);
        }
        int start = b.Count;
        for (int i = 0; i < n; i++)
        {
            var bin = Vector3D.CrossProduct(tangent[i], normal[i]);
            double r = Radius(at[i] / total);
            for (int k = 0; k <= rings; k++)
            {
                double a = k * 2 * Math.PI / rings;
                var dir = normal[i] * Math.Cos(a) + bin * Math.Sin(a);
                b.Add(Path[i] + dir * r, dir, new Point(k / (double)rings, at[i] / total));
            }
        }
        int segs = Closed ? n : n - 1;
        for (int i = 0; i < segs; i++)
            for (int k = 0; k < rings; k++)
            {
                int a0 = start + i * (rings + 1) + k, a1 = a0 + 1;
                int j = (i + 1) % n;
                int b0 = start + j * (rings + 1) + k, b1 = b0 + 1;
                b.Quad(a0, a1, b1, b0);
            }
        if (!Closed)
        {
            Cap(b, 0, -tangent[0], Radius(0), normal[0], tangent[0], rings);
            Cap(b, n - 1, tangent[n - 1], Radius(1), normal[n - 1], tangent[n - 1], rings);
        }
    }

    private void Cap(MeshB b, int i, Vector3D facing, double r, Vector3D nrm, Vector3D tan, int rings)
    {
        if (r < 0.02) return;
        // A rounded end: a small dome instead of a flat disk.
        var bin = Vector3D.CrossProduct(tan, nrm);
        int center = b.Add(Path[i] + facing * r * 0.6, facing, new Point(0.5, 0.5));
        int first = b.Count;
        for (int k = 0; k <= rings; k++)
        {
            double a = k * 2 * Math.PI / rings;
            var dir = nrm * Math.Cos(a) + bin * Math.Sin(a);
            b.Add(Path[i] + dir * r, Norm(dir + facing * 0.4), new Point(0.5, 0.5));
        }
        for (int k = 0; k < rings; k++) b.Tri(center, first + k, first + k + 1);
    }

    private void LatheMesh(MeshB b, int around)
    {
        for (int s = 0; s + 1 < Profile.Length; s++)
        {
            var p0 = Profile[s]; var p1 = Profile[s + 1];
            double dr = p1.X - p0.X, dy = p1.Y - p0.Y, len = Math.Sqrt(dr * dr + dy * dy);
            if (len < 1e-9) continue;
            double nx = dy / len, ny = -dr / len;
            int first = b.Count;
            for (int k = 0; k <= around; k++)
            {
                double a = k * 2 * Math.PI / around, c = Math.Cos(a), si = Math.Sin(a);
                var n = new Vector3D(nx * c, ny, nx * si);
                b.Add(new Point3D(p0.X * c, p0.Y, p0.X * si), n, new Point(k / (double)around, 0));
                b.Add(new Point3D(p1.X * c, p1.Y, p1.X * si), n, new Point(k / (double)around, 1));
            }
            for (int k = 0; k < around; k++)
            {
                int a0 = first + k * 2;
                b.Quad(a0, a0 + 2, a0 + 3, a0 + 1);
            }
        }
    }

    private void ExtrudeMesh(MeshB b)
    {
        double cx = Poly.Average(p => p.X), cy = Poly.Average(p => p.Y), h = Depth / 2;
        int n = Poly.Length;
        foreach (double side in new[] { 1.0, -1.0 })
        {
            var nz = new Vector3D(0, 0, side);
            int c = b.Add(new Point3D(cx, cy, h * side), nz, new Point(0.5, 0.5));
            int first = b.Count;
            foreach (var p in Poly) b.Add(new Point3D(p.X, p.Y, h * side), nz, new Point(0.5, 0.5));
            for (int i = 0; i < n; i++) b.Tri(c, first + i, first + (i + 1) % n);
        }
        for (int i = 0; i < n; i++)
        {
            var a = Poly[i]; var z = Poly[(i + 1) % n];
            var nn = Norm(new Vector3D(z.Y - a.Y, -(z.X - a.X), 0));
            if (Vector3D.DotProduct(nn, new Vector3D((a.X + z.X) / 2 - cx, (a.Y + z.Y) / 2 - cy, 0)) < 0) nn = -nn;
            int s = b.Add(new Point3D(a.X, a.Y, h), nn, default);
            b.Add(new Point3D(z.X, z.Y, h), nn, default);
            b.Add(new Point3D(z.X, z.Y, -h), nn, default);
            b.Add(new Point3D(a.X, a.Y, -h), nn, default);
            b.Quad(s, s + 1, s + 2, s + 3);
        }
    }

    private bool InPoly(double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = Poly.Length - 1; i < Poly.Length; j = i++)
            if ((Poly[i].Y > y) != (Poly[j].Y > y) && x < (Poly[j].X - Poly[i].X) * (y - Poly[i].Y) / (Poly[j].Y - Poly[i].Y) + Poly[i].X) inside = !inside;
        return inside;
    }

    private static double SegDist(Point p, Point a, Point b)
    {
        var ab = b - a; double len = ab.LengthSquared;
        double t = len < 1e-12 ? 0 : Math.Clamp(((p - a) * ab) / len, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    public static double Sq(double v) => v * v;
    public static double Pw(double v, double e) => Math.Pow(v, e);
    public static double Sp(double v, double e) => Math.Sign(v) * Math.Pow(Math.Abs(v), e);
    public static Vector3D Norm(Vector3D v) { double l = v.Length; return l < 1e-12 ? new Vector3D(0, 1, 0) : v / l; }

    // ---------- shapes for outlines ----------

    public static Point[] Star(int points, double outer, double inner, double spin = 90)
    {
        var list = new Point[points * 2];
        for (int i = 0; i < points * 2; i++)
        {
            double a = (spin + i * 180.0 / points) * Math.PI / 180, r = i % 2 == 0 ? outer : inner;
            list[i] = new Point(Math.Cos(a) * r, Math.Sin(a) * r);
        }
        return list;
    }

    public static Point[] Heart(double size)
    {
        var list = new List<Point>();
        for (int i = 0; i < 28; i++)
        {
            double t = i * 2 * Math.PI / 28;
            double x = 16 * Math.Pow(Math.Sin(t), 3), y = 13 * Math.Cos(t) - 5 * Math.Cos(2 * t) - 2 * Math.Cos(3 * t) - Math.Cos(4 * t);
            list.Add(new Point(x / 16 * size, (y + 2) / 16 * size));
        }
        return list.ToArray();
    }
}

/// <summary>Placement helpers. WPF multiplies row vectors, so each step applies after the ones before it.</summary>
internal static class Mat
{
    public static Matrix3D At(Point3D at, double rotX = 0, double rotY = 0, double rotZ = 0, double sx = 1, double sy = 1, double sz = 1)
    {
        var m = Matrix3D.Identity;
        m.Scale(new Vector3D(sx, sy, sz));
        if (rotZ != 0) m.Rotate(new Quaternion(new Vector3D(0, 0, 1), rotZ));
        if (rotX != 0) m.Rotate(new Quaternion(new Vector3D(1, 0, 0), rotX));
        if (rotY != 0) m.Rotate(new Quaternion(new Vector3D(0, 1, 0), rotY));
        m.Translate((Vector3D)at);
        return m;
    }

    /// <summary>Turns +z to the normal (a face feature lies on the surface), then moves it there.</summary>
    public static Matrix3D Facing(Point3D at, Vector3D n, double spin = 0)
    {
        n = Solid.Norm(n);
        var m = Matrix3D.Identity;
        if (spin != 0) m.Rotate(new Quaternion(new Vector3D(0, 0, 1), spin));
        m.Rotate(new Quaternion(new Vector3D(1, 0, 0), -Math.Asin(Math.Clamp(n.Y, -1, 1)) * 180 / Math.PI));
        m.Rotate(new Quaternion(new Vector3D(0, 1, 0), Math.Atan2(n.X, n.Z) * 180 / Math.PI));
        m.Translate((Vector3D)at);
        return m;
    }

    public static Matrix3D Then(this Matrix3D first, Matrix3D second) => first * second;
}

/// <summary>Collects vertices and triangles; triangles are wound so their front faces the way the normals point.</summary>
internal sealed class MeshB
{
    private readonly List<Point3D> _p = new();
    private readonly List<Vector3D> _n = new();
    private readonly List<Point> _t = new();
    private readonly List<int> _i = new();

    public int Count => _p.Count;

    public int Add(Point3D p, Vector3D n, Point t) { _p.Add(p); _n.Add(n); _t.Add(t); return _p.Count - 1; }

    public void Tri(int a, int b, int c)
    {
        var face = Vector3D.CrossProduct(_p[b] - _p[a], _p[c] - _p[a]);
        if (face.LengthSquared < 1e-14) return;
        if (Vector3D.DotProduct(face, _n[a] + _n[b] + _n[c]) < 0) (b, c) = (c, b);
        _i.Add(a); _i.Add(b); _i.Add(c);
    }

    public void Quad(int a, int b, int c, int d) { Tri(a, b, c); Tri(a, c, d); }

    /// <summary>A surface from u, v in 0..1 with normals taken from the surface itself, pointing away from the middle.</summary>
    public void Param(Func<double, double, Point3D> f, int nu, int nv)
    {
        int first = Count;
        const double d = 1e-3;
        for (int j = 0; j <= nv; j++)
            for (int i = 0; i <= nu; i++)
            {
                double u = i / (double)nu, v = j / (double)nv;
                var p = f(u, v);
                var du = f(u + d, v) - f(u - d, v);
                var dv = f(u, Math.Min(1, v + d)) - f(u, Math.Max(0, v - d));
                var n = Vector3D.CrossProduct(du, dv);
                if (n.LengthSquared < 1e-12) n = (Vector3D)p;
                n = Solid.Norm(n);
                if (Vector3D.DotProduct(n, (Vector3D)p) < 0) n = -n;
                Add(p, n, new Point(u, 1 - v));
            }
        for (int j = 0; j < nv; j++)
            for (int i = 0; i < nu; i++)
            {
                int a = first + j * (nu + 1) + i;
                Quad(a, a + 1, a + nu + 2, a + nu + 1);
            }
    }

    public void Apply(Matrix3D m)
    {
        if (m.IsIdentity) return;
        var nm = m;
        nm.Invert();
        nm = new Matrix3D(nm.M11, nm.M21, nm.M31, 0, nm.M12, nm.M22, nm.M32, 0, nm.M13, nm.M23, nm.M33, 0, 0, 0, 0, 1); // inverse transpose
        for (int i = 0; i < _p.Count; i++) { _p[i] = m.Transform(_p[i]); _n[i] = Solid.Norm(nm.Transform(_n[i])); }
    }

    public MeshGeometry3D Done()
    {
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection(_p), Normals = new Vector3DCollection(_n),
            TextureCoordinates = new PointCollection(_t), TriangleIndices = new Int32Collection(_i),
        };
        mesh.Freeze();
        return mesh;
    }

    /// <summary>The same surface pushed out along its normals: drawn from behind only, it becomes a dark outline.</summary>
    public MeshGeometry3D Hull(double width)
    {
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection(_p.Select((p, i) => p + _n[i] * width)),
            Normals = new Vector3DCollection(_n), TriangleIndices = new Int32Collection(_i),
        };
        mesh.Freeze();
        return mesh;
    }
}

/// <summary>Turns shapes into cubes on one shared grid, so all parts line up like a voxel model.</summary>
internal static class Voxels
{
    private static readonly Vector3D[] Dirs = { new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1) };

    /// <summary>
    /// Fills the cells whose middle lies in a shape (the last shape wins, so details paint over the body) and keeps only
    /// faces that are open to the air. Cells in <paramref name="solid"/> (another part, e.g. the body) count as closed;
    /// with <paramref name="decal"/> the cells that sit in that part are drawn a hair larger, so they paint over it.
    /// </summary>
    public static (Model3DGroup Model, HashSet<(int, int, int)> Cells) Build(IReadOnlyList<Solid> shapes, double size, HashSet<(int, int, int)> solid, bool decal, Func<Color, Material> material)
    {
        var box = Rect3D.Empty;
        foreach (var s in shapes) box.Union(s.Bounds());
        var cells = new Dictionary<(int, int, int), Solid>();
        var colors = new Dictionary<(int, int, int), Color>();
        if (!box.IsEmpty)
        {
            int x0 = (int)Math.Floor(box.X / size) - 1, x1 = (int)Math.Ceiling((box.X + box.SizeX) / size) + 1;
            int y0 = (int)Math.Floor(box.Y / size) - 1, y1 = (int)Math.Ceiling((box.Y + box.SizeY) / size) + 1;
            int z0 = (int)Math.Floor(box.Z / size) - 1, z1 = (int)Math.Ceiling((box.Z + box.SizeZ) / size) + 1;
            var boxes = shapes.Select(s => s.Bounds()).ToArray();
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                    {
                        var c = new Point3D((x + 0.5) * size, (y + 0.5) * size, (z + 0.5) * size);
                        for (int i = shapes.Count - 1; i >= 0; i--)
                        {
                            var s = shapes[i];
                            double m = s.Decal || s.Kind == Solid.Shape.Tube ? size * 0.5 : 0;
                            var b = boxes[i];
                            if (c.X < b.X - m - size || c.X > b.X + b.SizeX + m + size || c.Y < b.Y - m - size || c.Y > b.Y + b.SizeY + m + size || c.Z < b.Z - m - size || c.Z > b.Z + b.SizeZ + m + size) continue;
                            if (!s.Inside(c, m)) continue;
                            var key = (x, y, z);
                            if (!decal && solid != null && solid.Contains(key)) break; // already part of the body
                            cells[key] = s;
                            colors[key] = s.Paint?.Invoke(c) ?? s.Color;
                            break;
                        }
                    }
        }

        var builders = new Dictionary<(Color, bool), MeshB>();
        foreach (var (key, shape) in cells)
        {
            var (x, y, z) = key;
            var color = colors[key];
            if (shape.Glow <= 0 && !shape.Decal)
            {
                // A little variation from cube to cube, like a painted voxel model.
                uint h = (uint)(x * 73856093 ^ y * 19349663 ^ z * 83492791);
                double f = (h % 3) switch { 0 => 1.0, 1 => 0.94, _ => 1.05 };
                color = Color.FromRgb((byte)Math.Clamp(color.R * f, 0, 255), (byte)Math.Clamp(color.G * f, 0, 255), (byte)Math.Clamp(color.B * f, 0, 255));
            }
            bool over = decal && solid != null && solid.Contains(key);
            double grow = over ? 1.035 : 1;
            var center = new Point3D((x + 0.5) * size, (y + 0.5) * size, (z + 0.5) * size);
            var mk = (color, shape.Glow > 0);
            if (!builders.TryGetValue(mk, out var b)) builders[mk] = b = new MeshB();
            foreach (var d in Dirs)
            {
                var n = (x + (int)d.X, y + (int)d.Y, z + (int)d.Z);
                if (cells.ContainsKey(n) || (solid != null && solid.Contains(n))) continue;
                // The face: a square on the side d of the cube.
                var u = Math.Abs(d.X) > 0 ? new Vector3D(0, 1, 0) : new Vector3D(1, 0, 0);
                var v = Vector3D.CrossProduct(d, u);
                double hs = size / 2 * grow;
                var mid = center + d * hs;
                int a0 = b.Add(mid - u * hs - v * hs, d, default);
                b.Add(mid + u * hs - v * hs, d, default);
                b.Add(mid + u * hs + v * hs, d, default);
                b.Add(mid - u * hs + v * hs, d, default);
                b.Quad(a0, a0 + 1, a0 + 2, a0 + 3);
            }
        }
        var group = new Model3DGroup();
        foreach (var ((color, glow), b) in builders)
        {
            if (b.Count == 0) continue;
            var mat = material(color);
            if (glow)
            {
                var g = new MaterialGroup();
                g.Children.Add(mat);
                g.Children.Add(new EmissiveMaterial(Brush(Color.FromRgb((byte)(color.R * 0.6), (byte)(color.G * 0.6), (byte)(color.B * 0.6)))));
                g.Freeze();
                mat = g;
            }
            group.Children.Add(new GeometryModel3D(b.Done(), mat));
        }
        group.Freeze();
        return (group, new HashSet<(int, int, int)>(cells.Keys));
    }

    public static SolidColorBrush Brush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}

/// <summary>Draws a pattern onto a blob as a texture, by asking the pattern for the color at each point of the surface.</summary>
internal static class Paint
{
    public static ImageBrush Texture(Solid s, int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double u = (x + 0.5) / w, v = 1 - (y + 0.5) / h;
                var c = s.Paint(s.M.Transform(s.Param(u, v)));
                int i = (y * w + x) * 4;
                px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = 255;
            }
        var img = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        img.Freeze();
        var brush = new ImageBrush(img) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1), Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }
}
