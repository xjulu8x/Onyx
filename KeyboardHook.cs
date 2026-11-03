using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DesktopOverlay;

/// <summary>
/// Catches a lone press of the Windows key and raises <see cref="WinKeyPressed"/> instead of opening the Windows Start menu.
/// Win+X combos (Win+A, Win+E, ...) pass through untouched.
/// </summary>
public static class KeyboardHook
{
    private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
    private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_MASK = 0xE8; // 0xE8 is unassigned; used to "spoil" the lone Win press
    private const uint LLKHF_INJECTED = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);

    public static event Action WinKeyPressed;

    private static readonly HookProc Proc = Callback; // kept alive for the GC
    private static IntPtr _hook;
    private static bool _winDown, _combo;

    public static void Install()
    {
        if (_hook != IntPtr.Zero) return;
        using var mod = Process.GetCurrentProcess().MainModule!;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, Proc, GetModuleHandle(mod.ModuleName), 0);
    }

    public static void Uninstall()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private static IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((kb.flags & LLKHF_INJECTED) == 0)
            {
                bool down = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
                if (kb.vkCode is VK_LWIN or VK_RWIN)
                {
                    if (down && !_winDown) { _winDown = true; _combo = false; }
                    else if (!down)
                    {
                        _winDown = false;
                        if (!_combo)
                        {
                            // Windows opens Start/Search on a "clean" Win release; a dummy key in between cancels that.
                            // The real release is held back and re-sent after the dummy key, so the order is fixed:
                            // sending the dummy key alongside the real release raced with the shell and sometimes lost.
                            const uint KEYUP = 2, EXTENDED = 1;
                            Win32.keybd_event(VK_MASK, 0, 0, UIntPtr.Zero);
                            Win32.keybd_event(VK_MASK, 0, KEYUP, UIntPtr.Zero);
                            Win32.keybd_event((byte)kb.vkCode, (byte)kb.scanCode, KEYUP | EXTENDED, UIntPtr.Zero);
                            System.Windows.Application.Current.Dispatcher.BeginInvoke(() => WinKeyPressed?.Invoke());
                            return (IntPtr)1;
                        }
                    }
                }
                else if (down && _winDown) _combo = true;
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }
}
