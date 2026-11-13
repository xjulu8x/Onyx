using System.Runtime.InteropServices;

namespace DesktopOverlay;

/// <summary>Resolution and refresh rate of the main screen, read and changed through the Windows display API.</summary>
internal static class Display
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, IntPtr hwnd, int flags, IntPtr param);

    private const int CurrentSettings = -1, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
    private const int CDS_UPDATEREGISTRY = 0x1, CDS_TEST = 0x2;

    public readonly record struct Mode(int Width, int Height, int Hz)
    {
        public override string ToString() => $"{Width}x{Height} @ {Hz} Hz";
    }

    private static DEVMODE New() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    public static Mode Current()
    {
        var dm = New();
        return EnumDisplaySettings(null, CurrentSettings, ref dm) ? new Mode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency) : default;
    }

    /// <summary>Every mode the main screen offers in full color, largest first; repeats removed.</summary>
    public static List<Mode> Modes()
    {
        var list = new HashSet<Mode>();
        var dm = New();
        for (int i = 0; EnumDisplaySettings(null, i, ref dm); i++)
            if (dm.dmBitsPerPel >= 32 && dm.dmPelsWidth >= 800) list.Add(new Mode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency));
        return list.OrderByDescending(m => m.Width * m.Height).ThenByDescending(m => m.Width).ThenByDescending(m => m.Hz).ToList();
    }

    /// <summary>Switches the main screen; the mode is tested first, so a mode the screen can't show is never applied.</summary>
    public static bool Apply(Mode mode)
    {
        var dm = New();
        if (!EnumDisplaySettings(null, CurrentSettings, ref dm)) return false;
        dm.dmPelsWidth = mode.Width;
        dm.dmPelsHeight = mode.Height;
        dm.dmDisplayFrequency = mode.Hz;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
        if (ChangeDisplaySettingsEx(null, ref dm, IntPtr.Zero, CDS_TEST, IntPtr.Zero) != 0) return false;
        return ChangeDisplaySettingsEx(null, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero) == 0;
    }
}

/// <summary>
/// Color saturation of NVIDIA cards ("Digitale Farbanpassung", what NVIDIA Profile Inspector and vibrance tools change),
/// through the driver's own NVAPI. Without an NVIDIA card <see cref="Available"/> stays false.
/// </summary>
internal static class Vibrance
{
    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumDisplayFn(int index, out IntPtr display);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetDvcFn(IntPtr display, int output, ref DvcInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetDvcFn(IntPtr display, int output, ref DvcInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct DvcInfo { public uint Version; public int Current, Min, Max, Default; }

    private static bool _tried, _ok;
    private static EnumDisplayFn _enum;
    private static GetDvcFn _get;
    private static SetDvcFn _set;

    private static DvcInfo NewInfo() => new() { Version = (uint)Marshal.SizeOf<DvcInfo>() | (1u << 16) };

    private static bool Start()
    {
        if (_tried) return _ok;
        _tried = true;
        try
        {
            T Fn<T>(uint id) where T : Delegate { var p = QueryInterface(id); return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p); }
            var init = Fn<InitFn>(0x0150E828);
            _enum = Fn<EnumDisplayFn>(0x9ABDD40D);
            _get = Fn<GetDvcFn>(0x0E45002D);
            _set = Fn<SetDvcFn>(0x4A82C2B1);
            _ok = init != null && _enum != null && _get != null && _set != null && init() == 0;
        }
        catch { _ok = false; } // no NVIDIA driver
        return _ok;
    }

    public static bool Available => Start() && Read() != null;

    /// <summary>0..1 of the driver's range on the first screen, or null when it can't be read.</summary>
    public static double? Read()
    {
        if (!Start() || _enum(0, out var display) != 0) return null;
        var info = NewInfo();
        if (_get(display, 0, ref info) != 0 || info.Max <= info.Min) return null;
        return (info.Current - info.Min) / (double)(info.Max - info.Min);
    }

    /// <summary>The driver's default, as a share like <see cref="Read"/> (usually 50 %).</summary>
    public static double Default()
    {
        if (!Start() || _enum(0, out var display) != 0) return 0.5;
        var info = NewInfo();
        if (_get(display, 0, ref info) != 0 || info.Max <= info.Min) return 0.5;
        return (info.Default - info.Min) / (double)(info.Max - info.Min);
    }

    /// <summary>Sets the saturation on every NVIDIA screen.</summary>
    public static void Set(double share)
    {
        if (!Start()) return;
        for (int i = 0; _enum(i, out var display) == 0; i++)
        {
            var info = NewInfo();
            if (_get(display, 0, ref info) != 0) continue;
            info.Current = info.Min + (int)Math.Round(Math.Clamp(share, 0, 1) * (info.Max - info.Min));
            _set(display, 0, ref info);
        }
    }
}
