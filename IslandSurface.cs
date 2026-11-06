using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace DesktopOverlay;

/// <summary>
/// The body of the island, drawn by the Windows compositor in a window of its own below the content. Its spring animations
/// run on the GPU, so they stay smooth and cost this process almost nothing; repainting the transparent WPF window for
/// every frame is what made the island stutter. Windows with "Transparenzeffekte" on also get a real blur behind it.
/// </summary>
internal sealed class IslandSurface
{
    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool isTopmost, out IntPtr target);
        void EnsureOnThread(uint threadId);
    }

    [StructLayout(LayoutKind.Sequential)] private struct QueueOptions { public int Size, ThreadType, ApartmentType; }
    [StructLayout(LayoutKind.Sequential)] private struct AccentPolicy { public int AccentState, AccentFlags; public uint Color; public int AnimationId; }
    [StructLayout(LayoutKind.Sequential)] private struct CompositionData { public int Attribute; public IntPtr Data; public int Size; }
    [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(QueueOptions options, out IntPtr controller);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr h, ref CompositionData data);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr h, IntPtr region, bool redraw);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);

    private static IntPtr _queue; // has to live as long as the compositor

    private readonly HwndSource _window;
    private readonly Compositor _compositor;
    private readonly DesktopWindowTarget _target; // the visuals disappear if this is collected
    private readonly ContainerVisual _pill;
    private readonly SpriteVisual _tint, _glow;
    private readonly ContainerVisual _equalizer;
    private readonly SpriteVisual[] _bars = new SpriteVisual[4];
    private bool _barsMoving;
    private readonly CompositionRoundedRectangleGeometry _shape;
    private readonly CompositionSpriteShape _rim;
    private readonly bool _blur;
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(520) };
    private int _width, _height;
    private Vector2 _size;
    private float _top;

    public IslandSurface()
    {
        if (_queue == IntPtr.Zero) CreateDispatcherQueueController(new QueueOptions { Size = 12, ThreadType = 2 }, out _queue);

        const int WS_POPUP = unchecked((int)0x80000000);
        const int NOREDIRECT = 0x00200000, TOOL = 0x80, TOPMOST = 0x8, NOACTIVATE = 0x08000000;
        _window = new HwndSource(new HwndSourceParameters("OnyxIsland", 1, 1) { WindowStyle = WS_POPUP, ExtendedWindowStyle = NOREDIRECT | TOOL | TOPMOST | NOACTIVATE });

        // Blur needs the Windows setting; without it Windows would only draw black, so the glass look stands alone then.
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            _blur = key?.GetValue("EnableTransparency") is int on && on != 0;
        if (_blur) EnableBackdrop();

        _compositor = new Compositor();
        _compositor.As<ICompositorDesktopInterop>().CreateDesktopWindowTarget(_window.Handle, true, out var raw);
        _target = DesktopWindowTarget.FromAbi(raw);

        _pill = _compositor.CreateContainerVisual();
        _target.Root = _pill;
        if (_blur)
        {
            var backdrop = _compositor.CreateSpriteVisual();
            backdrop.Brush = _compositor.CreateHostBackdropBrush();
            backdrop.RelativeSizeAdjustment = Vector2.One;
            _pill.Children.InsertAtTop(backdrop);
        }
        _tint = _compositor.CreateSpriteVisual();
        _tint.RelativeSizeAdjustment = Vector2.One;
        _pill.Children.InsertAtTop(_tint);

        var sheen = _compositor.CreateSpriteVisual();
        sheen.Brush = Gradient(Color.FromArgb(0x22, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 0.55f);
        sheen.RelativeSizeAdjustment = Vector2.One;
        _pill.Children.InsertAtTop(sheen);

        _glow = _compositor.CreateSpriteVisual();
        _glow.RelativeSizeAdjustment = new Vector2(1, 0.32f);
        _glow.RelativeOffsetAdjustment = new Vector3(0, 0.68f, 0);
        _glow.Opacity = 0;
        _pill.Children.InsertAtTop(_glow);

        _equalizer = _compositor.CreateContainerVisual();
        _pill.Children.InsertAtTop(_equalizer);
        for (int i = 0; i < _bars.Length; i++)
        {
            _bars[i] = _compositor.CreateSpriteVisual();
            _bars[i].Brush = _compositor.CreateColorBrush(Color.FromArgb(0xFF, 255, 255, 255));
            _equalizer.Children.InsertAtTop(_bars[i]);
        }

        _shape = _compositor.CreateRoundedRectangleGeometry();
        _pill.Clip = _compositor.CreateGeometricClip(_shape);

        var edge = _compositor.CreateShapeVisual();
        edge.RelativeSizeAdjustment = Vector2.One;
        _rim = _compositor.CreateSpriteShape(_shape);
        _rim.StrokeThickness = 2; // half of it is clipped away, which keeps the edge crisp
        edge.Shapes.Add(_rim);
        _pill.Children.InsertAtTop(edge);

        _settle.Tick += (_, _) => { _settle.Stop(); ClipToPill(0); };
    }

    private void EnableBackdrop()
    {
        int on = 1;
        DwmSetWindowAttribute(_window.Handle, 17 /* DWMWA_USE_HOSTBACKDROPBRUSH */, ref on, 4);
        var accent = new AccentPolicy { AccentState = 5 };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
        Marshal.StructureToPtr(accent, memory, false);
        var data = new CompositionData { Attribute = 19, Data = memory, Size = Marshal.SizeOf<AccentPolicy>() };
        SetWindowCompositionAttribute(_window.Handle, ref data);
        Marshal.FreeHGlobal(memory);
    }

    private CompositionLinearGradientBrush Gradient(Color from, Color to, float end = 1)
    {
        var brush = _compositor.CreateLinearGradientBrush();
        brush.StartPoint = new Vector2(0.5f, 0);
        brush.EndPoint = new Vector2(0.5f, 1);
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(0, from));
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(end, to));
        return brush;
    }

    /// <summary>The window behind the content; all sizes are device pixels.</summary>
    public void Place(int x, int y, int width, int height)
    {
        _width = width;
        _height = height;
        Native.SetWindowPos(_window.Handle, Native.HWND_TOPMOST, x, y, width, height, Native.SWP_NOACTIVATE);
    }

    public void SetVisible(bool visible) => ShowWindow(_window.Handle, visible ? 4 /* SW_SHOWNOACTIVATE */ : 0);

    /// <summary>Keeps the surface directly below the content window, which is raised right after this.</summary>
    public void Raise() => Native.SetWindowPos(_window.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

    /// <summary>Tint and edge. With real blur behind it the tint can be lighter.</summary>
    public void SetLook(System.Windows.Media.Color tint, double opacity, System.Windows.Media.Color? rim)
    {
        byte alpha = (byte)(255 * opacity * (_blur ? 0.6 : 0.94)); // always a little see-through: that is the glass
        _tint.Brush = _compositor.CreateColorBrush(Color.FromArgb(alpha, tint.R, tint.G, tint.B));
        var top = rim ?? System.Windows.Media.Color.FromRgb(255, 255, 255);
        _rim.StrokeBrush = Gradient(Color.FromArgb(rim == null ? (byte)0x50 : (byte)0xC0, top.R, top.G, top.B), Color.FromArgb(rim == null ? (byte)0x10 : (byte)0x50, top.R, top.G, top.B));
    }

    public void SetGlow(float opacity) => _glow.Opacity = opacity;

    public void SetGlowColor(System.Windows.Media.Color c) =>
        _glow.Brush = Gradient(Color.FromArgb(0, c.R, c.G, c.B), Color.FromArgb(0xFF, c.R, c.G, c.B));

    /// <summary>The equalizer of the small island. Its bars loop on the GPU, so playing music costs this process nothing.</summary>
    public void SetEqualizer(bool playing, bool visible, float scale, float x, float y)
    {
        _equalizer.Offset = new Vector3(x, y, 0);
        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, visible ? 1 : 0);
        fade.Duration = TimeSpan.FromMilliseconds(visible ? 220 : 90);
        _equalizer.StartAnimation("Opacity", fade);

        for (int i = 0; i < _bars.Length; i++)
        {
            var bar = _bars[i];
            bar.Size = new Vector2(3 * scale, 14 * scale);
            bar.Offset = new Vector3(i * 5 * scale, 0, 0);
            bar.CenterPoint = new Vector3(1.5f * scale, 7 * scale, 0);
        }
        bool move = playing && visible;
        if (move == _barsMoving) { if (!move) foreach (var b in _bars) b.Scale = new Vector3(1, 0.2f, 1); return; }
        _barsMoving = move;
        for (int i = 0; i < _bars.Length; i++)
        {
            _bars[i].StopAnimation("Scale.Y");
            if (!move) { _bars[i].Scale = new Vector3(1, 0.2f, 1); continue; }
            var loop = _compositor.CreateScalarKeyFrameAnimation();
            float[] heights = i % 2 == 0 ? new[] { 0.35f, 1f, 0.5f, 0.85f, 0.35f } : new[] { 0.8f, 0.4f, 1f, 0.3f, 0.8f };
            for (int key = 0; key < heights.Length; key++) loop.InsertKeyFrame(key / 4f, heights[key]);
            loop.Duration = TimeSpan.FromMilliseconds(760 + i * 130);
            loop.IterationBehavior = AnimationIterationBehavior.Forever;
            _bars[i].StartAnimation("Scale.Y", loop);
        }
    }

    /// <summary>Moves the pill to its new shape. "open" uses the springy curve, closing settles without bouncing.</summary>
    public void MoveTo(float width, float height, float top, float radius, bool open, bool animate)
    {
        _size = new Vector2(width, height);
        _top = top;
        var offset = new Vector3((_width - width) / 2, top, 0);
        var corners = new Vector2(radius, radius);
        _settle.Stop();

        if (!animate)
        {
            _pill.StopAnimation("Size"); _pill.StopAnimation("Offset");
            _shape.StopAnimation("Size"); _shape.StopAnimation("CornerRadius");
            _pill.Size = _size; _pill.Offset = offset; _shape.Size = _size; _shape.CornerRadius = corners;
            ClipToPill(0);
            return;
        }

        // Room for the overshoot while opening; after closing the window shrinks back once the pill has settled.
        if (open) ClipToPill(24);
        var (damping, period) = Theme.Spring(open);
        var size = _compositor.CreateSpringVector2Animation();
        size.DampingRatio = damping; size.Period = period; size.FinalValue = _size;
        _pill.StartAnimation("Size", size);
        _shape.StartAnimation("Size", size);

        var move = _compositor.CreateSpringVector3Animation();
        move.DampingRatio = damping; move.Period = period; move.FinalValue = offset;
        _pill.StartAnimation("Offset", move);

        var round = _compositor.CreateSpringVector2Animation();
        round.DampingRatio = 1; round.Period = period; round.FinalValue = corners;
        _shape.StartAnimation("CornerRadius", round);
        if (!open) _settle.Start();
    }

    /// <summary>
    /// The window only covers the pill (plus margin), a plain rectangle: clicks next to the island reach what is below it.
    /// </summary>
    private void ClipToPill(float margin)
    {
        int w = (int)(_size.X + 2 * margin), h = (int)(_size.Y + _top + margin);
        int left = (_width - w) / 2;
        SetWindowRgn(_window.Handle, CreateRectRgn(left, 0, left + w, h), true);
    }
}
