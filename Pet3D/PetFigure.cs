using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace DesktopOverlay;

/// <summary>Everything that moves, for one frame. Angles in degrees, distances in world units (grid pixels).</summary>
internal struct PetRig
{
    public double Yaw, Roll, Pitch, ArmL, ArmR, SwingL, SwingR, Ears, HatRot, HatLift, FootL, FootR, StepL, StepR, Sink, LightX, LightY;
    public FaceState Face;
    public int Wave;
}

/// <summary>
/// The character as a real 3D model in a Viewport3D: built from <see cref="PetLook"/> once per look, then only its
/// joints turn from frame to frame (the GPU draws it). Two styles: smooth, shiny vinyl toy or voxels.
/// </summary>
internal sealed class PetFigure
{
    public const double WorldW = 38, WorldH = 42, CenterY = 16.5, Pitch = 12; // just room for raised arms and tall hats: a smaller 3D surface is cheaper

    public readonly Viewport3D View;
    private readonly OrthographicCamera _camera = new();
    private readonly bool _compact;
    private readonly Model3DGroup _figure = new(), _feetL = new(), _feetR = new(), _bodyRig = new(), _body = new(), _face = new(), _armL = new(), _armR = new(), _ears = new(), _hat = new();
    private readonly AxisAngleRotation3D _yaw = new(new Vector3D(0, 1, 0), 0), _roll = new(new Vector3D(0, 0, 1), 0), _pitch = new(new Vector3D(1, 0, 0), 0);
    private readonly AxisAngleRotation3D _armLz = new(new Vector3D(0, 0, 1), 0), _armRz = new(new Vector3D(0, 0, 1), 0), _armLx = new(new Vector3D(1, 0, 0), 0), _armRx = new(new Vector3D(1, 0, 0), 0);
    private readonly AxisAngleRotation3D _earZ = new(new Vector3D(0, 0, 1), 0), _hatZ = new(new Vector3D(0, 0, 1), 0);
    private readonly RotateTransform3D _rollT, _pitchT, _armLzT, _armRzT, _armLxT, _armRxT, _earT, _hatRT;
    private readonly TranslateTransform3D _sink = new(), _footLT = new(), _footRT = new(), _hatT = new();
    private readonly ScaleTransform3D _head = new();
    private readonly AmbientLight _amb = new();
    private readonly DirectionalLight _key = new(), _fill = new(), _rim = new();

    private string _lookKey;
    private PetLook _look;
    private bool _voxel;
    private HashSet<(int, int, int)> _bodyCells;
    private readonly Dictionary<FaceState, Model3D> _faces = new();
    private readonly Dictionary<int, (Model3D Model, HashSet<(int, int, int)> Cells)> _bodies = new();
    private readonly Dictionary<Color, Material> _materials = new();
    private FaceState? _faceNow;
    private int _waveNow = -1;

    public int Rows { get; private set; } = 30;

    public PetFigure(bool compact)
    {
        _compact = compact;
        _rollT = new RotateTransform3D(_roll); _pitchT = new RotateTransform3D(_pitch);
        _armLzT = new RotateTransform3D(_armLz); _armRzT = new RotateTransform3D(_armRz);
        _armLxT = new RotateTransform3D(_armLx); _armRxT = new RotateTransform3D(_armRx);
        _earT = new RotateTransform3D(_earZ); _hatRT = new RotateTransform3D(_hatZ);

        _figure.Transform = new RotateTransform3D(_yaw);
        _feetL.Transform = _footLT;
        _feetR.Transform = _footRT;
        var rig = new Transform3DGroup();
        rig.Children.Add(_head); rig.Children.Add(_rollT); rig.Children.Add(_pitchT); rig.Children.Add(_sink);
        _bodyRig.Transform = rig;
        _armL.Transform = Group(_armLzT, _armLxT);
        _armR.Transform = Group(_armRzT, _armRxT);
        _ears.Transform = _earT;
        _hat.Transform = Group(_hatRT, _hatT);

        _figure.Children.Add(_feetL); _figure.Children.Add(_feetR); _figure.Children.Add(_bodyRig);
        foreach (var part in new[] { _body, _face, _armL, _armR, _ears, _hat }) _bodyRig.Children.Add(part);

        var scene = new Model3DGroup();
        scene.Children.Add(_amb); scene.Children.Add(_key); scene.Children.Add(_fill); scene.Children.Add(_rim);
        scene.Children.Add(_figure);

        double p = Pitch * Math.PI / 180;
        var back = new Vector3D(0, Math.Sin(p), Math.Cos(p));
        _camera.Position = new Point3D(0, CenterY, 0) + back * 100;
        _camera.LookDirection = -back;
        _camera.UpDirection = new Vector3D(0, 1, 0);
        _camera.Width = WorldW;
        _camera.NearPlaneDistance = 40;
        _camera.FarPlaneDistance = 160;

        View = new Viewport3D { Camera = _camera, ClipToBounds = false, IsHitTestVisible = false };
        View.Children.Add(new ModelVisual3D { Content = scene });
        RenderOptions.SetBitmapScalingMode(View, BitmapScalingMode.Linear); // textures smooth, even inside the pixel-sharp pet
        RenderOptions.SetEdgeMode(View, EdgeMode.Unspecified);
    }

    private static Transform3DGroup Group(params Transform3D[] items)
    {
        var g = new Transform3DGroup();
        foreach (var t in items) g.Children.Add(t);
        return g;
    }

    /// <summary>Where the floor under the middle of the model is in the viewport, for one world unit = <paramref name="unit"/> DIPs.</summary>
    public static double GroundY(double unit) => (WorldH / 2 + CenterY * Math.Cos(Pitch * Math.PI / 180)) * unit;

    // ---------- building ----------

    private static readonly string[] Ignore = { "PetName", "PetFx", "PetBehavior", "PetSize", "PetSpeed", "PetInNotch", "PetDance" };
    private static readonly PropertyInfo[] LookProps = typeof(Settings).GetProperties().Where(p => p.Name.StartsWith("Pet") && !Ignore.Contains(p.Name)).ToArray();

    public static string KeyOf(Settings s) => string.Join("|", LookProps.Select(p => p.GetValue(s)));

    private void Ensure(Settings s, int wave)
    {
        string key = KeyOf(s);
        if (key != _lookKey)
        {
            _lookKey = key;
            _look = new PetLook(s);
            _voxel = s.PetStyle == 1;
            _faces.Clear();
            _bodies.Clear();
            _materials.Clear();
            _faceNow = null;
            _waveNow = -1;
            Rows = _look.Rows();
            Lights(s);
            var L = _look;
            _head.CenterY = L.Bottom; _head.ScaleX = _head.ScaleY = _head.ScaleZ = L.Head;
            _rollT.CenterY = _pitchT.CenterY = L.Cy;
            foreach (var (t, side) in new[] { (_armLzT, -1), (_armLxT, -1), (_armRzT, 1), (_armRxT, 1) })
            {
                var c = L.Shoulder(side);
                t.CenterX = c.X; t.CenterY = c.Y; t.CenterZ = c.Z;
            }
            _earT.CenterX = L.EarPivot.X; _earT.CenterY = L.EarPivot.Y;
            _hatRT.CenterX = L.HatPivot.X; _hatRT.CenterY = L.HatPivot.Y;

            // The body first: in the voxel style the other parts leave out the cubes it already fills.
            var (body, cells) = BodyModel(wave);
            _bodyCells = cells;
            Set(_body, body);
            _waveNow = L.Shape == 3 ? wave : 0;
            Set(_armL, Part(new[] { L.Arm(-1) }));
            Set(_armR, Part(new[] { L.Arm(1) }));
            Set(_feetL, Part(new[] { L.Foot(-1) }));
            Set(_feetR, Part(new[] { L.Foot(1) }));
            Set(_ears, Part(L.Ears()));
            Set(_hat, Part(L.Hat()));
        }
        if (_look.Shape == 3 && wave != _waveNow)
        {
            _waveNow = wave;
            Set(_body, BodyModel(wave).Model);
        }
    }

    private (Model3D Model, HashSet<(int, int, int)> Cells) BodyModel(int wave)
    {
        if (_bodies.TryGetValue(wave, out var cached)) return cached;
        var shapes = new List<Solid> { _look.BodySolid(wave) };
        shapes.AddRange(_look.BodyExtras());
        (Model3D, HashSet<(int, int, int)>) result;
        if (_voxel)
        {
            var (m, c) = Voxels.Build(shapes, VoxelSize, null, false, VoxelMaterial);
            result = (m, c);
        }
        else result = (Soft(shapes), null);
        return _bodies[wave] = result;
    }

    private double VoxelSize => _compact ? 1.05 : 0.72;

    private Model3D Part(IEnumerable<Solid> shapes, bool decal = false)
    {
        var list = shapes.Where(s => s != null).ToList();
        if (list.Count == 0) return null;
        return _voxel ? Voxels.Build(list, VoxelSize, _bodyCells, decal, VoxelMaterial).Model : Soft(list);
    }

    private static void Set(Model3DGroup slot, Model3D content)
    {
        slot.Children.Clear();
        if (content != null) slot.Children.Add(content);
    }

    // ---------- materials ----------

    private Material VoxelMaterial(Color c)
    {
        if (_materials.TryGetValue(c, out var m)) return m;
        var d = new DiffuseMaterial(Voxels.Brush(c));
        if (_look.S.PetMaterial == 3)
        {
            var g = new MaterialGroup();
            g.Children.Add(d);
            g.Children.Add(new SpecularMaterial(Voxels.Brush(Color.FromRgb(0x70, 0x70, 0x70)), 30));
            m = g;
        }
        else m = d;
        m.Freeze();
        return _materials[c] = m;
    }

    private Model3D Soft(List<Solid> shapes)
    {
        var group = new Model3DGroup();
        bool outline = _look.S.PetOutline;
        double width = _compact ? 0.75 : 0.36;
        foreach (var s in shapes)
        {
            var box = s.Bounds();
            double size = Math.Max(box.SizeX, Math.Max(box.SizeY, box.SizeZ));
            int detail = size > 12 ? (_compact ? 26 : 44) : size > 4 ? (_compact ? 14 : 22) : (_compact ? 10 : 14);
            var (mesh, hull) = s.Mesh(detail, outline && s.Outline ? width : 0);
            group.Children.Add(new GeometryModel3D(mesh, SoftMaterial(s, size)));
            if (hull != null)
            {
                var ink = new MaterialGroup();
                ink.Children.Add(new DiffuseMaterial(Brushes.Black));
                ink.Children.Add(new EmissiveMaterial(Voxels.Brush(PetLook.Mix(s.Color, Color.FromRgb(0x08, 0x08, 0x10), 0.82))));
                ink.Freeze();
                group.Children.Add(new GeometryModel3D(hull, null) { BackMaterial = ink });
            }
        }
        group.Freeze();
        return group;
    }

    /// <summary>Paint, shine and glow of one shape; the material setting changes how shiny everything but the face is.</summary>
    private Material SoftMaterial(Solid s, double size)
    {
        Brush brush = s.Paint != null && s.Kind is Solid.Shape.Blob or Solid.Shape.Ghost ? Paint.Texture(s, _compact ? 96 : 192, _compact ? 48 : 96) : Voxels.Brush(s.Color);
        var g = new MaterialGroup();
        var diffuse = new DiffuseMaterial(brush);
        int kind = _look.S.PetMaterial;
        double strength, power;
        if (s.Decal) { strength = s.Gloss * 0.55; power = 45; }
        else
            switch (kind)
            {
                case 0: strength = s.Gloss * 0.12; power = 6; break;          // matte
                case 2: strength = s.Gloss * 0.18; power = 5; break;          // velvet: soft sheen, light on the edges
                case 3: strength = s.Gloss * 0.95; power = 55; diffuse.Color = Color.FromRgb(0xA8, 0xA8, 0xB0); break; // metal
                default: strength = s.Gloss * 0.6; power = 38; break;         // glossy vinyl
            }
        g.Children.Add(diffuse);
        if (strength > 0.01)
        {
            var c = kind == 3 && !s.Decal ? _look.Lighter(s.Color, 0.5) : Colors.White;
            double k = Math.Min(1, strength);
            g.Children.Add(new SpecularMaterial(Voxels.Brush(Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k))), power));
        }
        double glow = s.Glow + (kind == 2 && !s.Decal ? 0.07 : 0);
        if (glow > 0) g.Children.Add(new EmissiveMaterial(Voxels.Brush(Color.FromRgb((byte)(s.Color.R * Math.Min(1, glow)), (byte)(s.Color.G * Math.Min(1, glow)), (byte)(s.Color.B * Math.Min(1, glow))))));
        g.Freeze();
        _ = size;
        return g;
    }

    private void Lights(Settings s)
    {
        // Flat, soft or strong shading; velvet adds light along the edges.
        var (amb, key, fill, rim) = s.PetShading switch { 0 => (0.66, 0.42, 0.10, 0.10), 1 => (0.48, 0.62, 0.16, 0.22), _ => (0.34, 0.80, 0.20, 0.34) };
        if (s.PetMaterial == 2) rim += 0.3;
        if (s.PetStyle == 1) { amb += 0.04; key += 0.05; }
        Color C(double v, int r, int g, int b) => Color.FromRgb((byte)Math.Min(255, r * v), (byte)Math.Min(255, g * v), (byte)Math.Min(255, b * v));
        _amb.Color = C(amb, 235, 238, 255);
        _key.Color = C(key, 255, 246, 232);
        _fill.Color = C(fill, 170, 190, 255);
        _rim.Color = C(rim, 255, 255, 255);
        _rim.Direction = new Vector3D(0, -0.7, 0.75);
    }

    // ---------- per frame ----------

    public void Pose(Settings s, in PetRig r)
    {
        Ensure(s, r.Wave);
        _yaw.Angle = r.Yaw;
        _roll.Angle = r.Roll;
        _pitch.Angle = r.Pitch;
        _armLz.Angle = r.ArmL; _armRz.Angle = r.ArmR;
        _armLx.Angle = r.SwingL; _armRx.Angle = r.SwingR;
        _earZ.Angle = r.Ears;
        _hatZ.Angle = r.HatRot;
        _hatT.OffsetY = r.HatLift;
        _footLT.OffsetY = r.FootL; _footLT.OffsetZ = r.StepL;
        _footRT.OffsetY = r.FootR; _footRT.OffsetZ = r.StepR;
        _sink.OffsetY = -r.Sink;

        if (_faceNow != r.Face)
        {
            _faceNow = r.Face;
            if (!_faces.TryGetValue(r.Face, out var face))
            {
                if (_faces.Count > 40) _faces.Clear();
                _faces[r.Face] = face = Part(_look.Face(r.Face), decal: true);
            }
            Set(_face, face);
        }

        // The key light comes from where the mouse is (or the sun), a cool fill from the other side.
        var toLight = Solid.Norm(new Vector3D(r.LightX, r.LightY, 0.95));
        _key.Direction = -toLight;
        _fill.Direction = -Solid.Norm(new Vector3D(-r.LightX * 0.9, -0.25, 0.7));
    }

    // ---------- pictures for the settings ----------

    private static readonly Dictionary<string, BitmapSource> Pictures = new();

    /// <summary>The character standing, turned a little, as a picture (drawn without a window).</summary>
    public static BitmapSource Picture(Settings s, int w, int h)
    {
        string key = KeyOf(s) + $"|{w}x{h}";
        if (Pictures.TryGetValue(key, out var cached)) return cached;
        if (Pictures.Count > 300) Pictures.Clear();
        var fig = new PetFigure(compact: true);
        fig.Pose(s, new PetRig { Yaw = 18, LightX = -0.45, LightY = 0.85, Face = new FaceState(false, false, false, PixelPet.Normal, 0, 0) });
        double rows = fig.Rows, width = Math.Max(30, rows * w / (double)h);
        fig._camera.Width = width;
        double p = Pitch * Math.PI / 180;
        var back = new Vector3D(0, Math.Sin(p), Math.Cos(p));
        fig._camera.Position = new Point3D(0, rows / 2 - 0.5, 0) + back * 100;
        fig.View.Width = w; fig.View.Height = h;
        fig.View.Measure(new Size(w, h));
        fig.View.Arrange(new Rect(0, 0, w, h));
        fig.View.UpdateLayout();
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(fig.View);
        rtb.Freeze();
        return Pictures[key] = rtb;
    }
}
