using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DesktopOverlay;

internal static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x80;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WM_WINDOWPOSCHANGING = 0x0046;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_BOTTOM = new(1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [StructLayout(LayoutKind.Sequential)]
    public struct AccentPolicy { public int AccentState; public int AccentFlags; public uint GradientColor; public int AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    public struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }

    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int value);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
    [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHQueryRecycleBin(string root, ref SHQUERYRBINFO info);

    /// <summary>Hides the window from Alt+Tab and keeps it from stealing focus. Desktop-level windows stay pinned below everything.</summary>
    public static void MakeOverlay(Window w, bool desktopLevel)
    {
        w.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            if (!desktopLevel) return;
            SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            HwndSource.FromHwnd(hwnd).AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                if (msg == WM_WINDOWPOSCHANGING && !WidgetMover.Editing) // while arranging, the widgets come to the front
                {
                    var pos = Marshal.PtrToStructure<WINDOWPOS>(lp);
                    pos.hwndInsertAfter = HWND_BOTTOM;
                    pos.flags &= ~SWP_NOZORDER;
                    Marshal.StructureToPtr(pos, lp, false);
                }
                return IntPtr.Zero;
            });
        };
    }

    public static void BringTopmost(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Taskbar rect in WPF device-independent units, or null if not found.</summary>
    public static Rect? TaskbarRect(Visual v)
    {
        var h = FindWindow("Shell_TrayWnd", null);
        if (h == IntPtr.Zero || !GetWindowRect(h, out var r)) return null;
        var src = PresentationSource.FromVisual(v);
        double sx = src?.CompositionTarget?.TransformFromDevice.M11 ?? 1, sy = src?.CompositionTarget?.TransformFromDevice.M22 ?? 1;
        return new Rect(r.Left * sx, r.Top * sy, (r.Right - r.Left) * sx, (r.Bottom - r.Top) * sy);
    }
}

// ---- Core Audio (master volume) ----
[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
}

[StructLayout(LayoutKind.Sequential)] internal struct PropertyKey { public Guid Format; public int Id; }
[StructLayout(LayoutKind.Explicit, Size = 24)] internal struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorCom { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int mask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object itf);
    [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IntPtr p);
    [PreserveSig] int UnregisterControlChangeNotify(IntPtr p);
    [PreserveSig] int GetChannelCount(out uint c);
    [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid ctx);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
    [PreserveSig] int GetMasterVolumeLevel(out float level);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint ch, float level, ref Guid ctx);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
    [PreserveSig] int GetChannelVolumeLevel(uint ch, out float level);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioMeterInformation
{
    [PreserveSig] int GetPeakValue(out float peak);
}

internal static class Volume
{
    private static IAudioEndpointVolume _cached;
    private static DateTime _cachedAt;
    private static IAudioMeterInformation _meter;
    private static DateTime _meterAt;

    /// <summary>Current output level, 0..1. Windows measures this anyway, so reading it is nearly free.</summary>
    public static float Peak()
    {
        try
        {
            if (_meter == null || (DateTime.UtcNow - _meterAt).TotalSeconds > 5)
            {
                _meterAt = DateTime.UtcNow;
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                if (en.GetDefaultAudioEndpoint(0, 1, out var dev) != 0) return 0;
                var iid = typeof(IAudioMeterInformation).GUID;
                dev.Activate(ref iid, 23, IntPtr.Zero, out var o);
                _meter = o as IAudioMeterInformation;
            }
            float peak = 0;
            _meter?.GetPeakValue(out peak);
            return peak;
        }
        catch { return 0; }
    }

    // Re-resolved every few seconds (cheap enough, and follows a changed default output device) instead of on every call.
    private static IAudioEndpointVolume Endpoint()
    {
        if (_cached != null && (DateTime.UtcNow - _cachedAt).TotalSeconds < 5) return _cached;
        _cachedAt = DateTime.UtcNow;
        return _cached = Resolve();
    }

    private static IAudioEndpointVolume Resolve()
    {
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            if (en.GetDefaultAudioEndpoint(0, 1, out var dev) != 0) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            dev.Activate(ref iid, 23, IntPtr.Zero, out var o);
            return o as IAudioEndpointVolume;
        }
        catch { return null; }
    }

    private static string _deviceName;
    private static DateTime _deviceAt;

    /// <summary>Name of the current output device, e.g. "Kopfhörer (USB)". Read at most every 5 seconds.</summary>
    public static string DeviceName()
    {
        if ((DateTime.UtcNow - _deviceAt).TotalSeconds < 5) return _deviceName;
        _deviceAt = DateTime.UtcNow;
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            if (en.GetDefaultAudioEndpoint(0, 1, out var dev) != 0) return _deviceName = null;
            if (dev.OpenPropertyStore(0, out var store) != 0) return _deviceName = null;
            var key = new PropertyKey { Format = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Id = 14 };
            if (store.GetValue(ref key, out var value) != 0 || value.Type != 31 /* VT_LPWSTR */) return _deviceName = null;
            _deviceName = Marshal.PtrToStringUni(value.Pointer);
            PropVariantClear(ref value);
            return _deviceName;
        }
        catch { return _deviceName = null; }
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);

    public static float Get() { var e = Endpoint(); if (e == null) return 0; e.GetMasterVolumeLevelScalar(out var v); return v; }
    public static void Set(float v) { var g = Guid.Empty; Endpoint()?.SetMasterVolumeLevelScalar(Math.Clamp(v, 0f, 1f), ref g); }
    public static bool IsMuted() { var e = Endpoint(); if (e == null) return false; e.GetMute(out var m); return m; }
    public static void ToggleMute() { var e = Endpoint(); if (e == null) return; var g = Guid.Empty; e.GetMute(out var m); e.SetMute(!m, ref g); }
}
