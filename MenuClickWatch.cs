using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Interop;

namespace DesktopOverlay;

/// <summary>
/// Closes an open context menu when the user clicks anywhere outside it. Needed because the overlay's windows never
/// take focus, so WPF itself doesn't see those clicks. The low-level mouse hook only exists while a menu is open.
/// </summary>
internal static class MenuClickWatch
{
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);

    private static IntPtr _hook;
    private static HookProc _proc; // kept alive for the GC
    private static ContextMenu _menu;
    private static IntPtr _menuHwnd;

    public static bool IsOpen => _menu != null;

    public static void Start(ContextMenu menu)
    {
        Stop();
        _menu = menu;
        _menuHwnd = (System.Windows.PresentationSource.FromVisual(menu) as HwndSource)?.Handle ?? IntPtr.Zero;
        menu.Closed += (_, _) => { if (_menu == menu) Stop(); };
        _proc = OnMouse;
        using var mod = Process.GetCurrentProcess().MainModule!;
        _hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _proc, GetModuleHandle(mod.ModuleName), 0);
    }

    private static void Stop()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _menu = null;
    }

    private static IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        const int WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207;
        if (code >= 0 && (wParam == WM_LBUTTONDOWN || wParam == WM_RBUTTONDOWN || wParam == WM_MBUTTONDOWN) && _menu != null)
        {
            var pt = Marshal.PtrToStructure<Native.POINT>(lParam);
            bool inside = _menuHwnd != IntPtr.Zero && Native.GetWindowRect(_menuHwnd, out var r)
                          && pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;
            if (!inside)
            {
                var menu = _menu;
                menu.Dispatcher.BeginInvoke(() => menu.IsOpen = false);
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }
}
