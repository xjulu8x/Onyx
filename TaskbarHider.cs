using System.Windows.Threading;

namespace DesktopOverlay;

/// <summary>Hides the Windows taskbar while the overlay runs (it is replaced by <see cref="TaskbarWindow"/>) and brings it back on exit.</summary>
public static class TaskbarHider
{
    private static DispatcherTimer _timer;

    public static void Start()
    {
        Hide();
        // Explorer sometimes re-shows the taskbar (Start menu, explorer restart); a cheap visibility check keeps it hidden.
        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => Hide(), Dispatcher.CurrentDispatcher);
        _timer.Start();
    }

    public static void Restore()
    {
        _timer?.Stop();
        foreach (var h in TaskbarWindows()) Win32.ShowWindow(h, Win32.SW_SHOWNA);
    }

    private static void Hide()
    {
        foreach (var h in TaskbarWindows())
            if (Win32.IsWindowVisible(h)) Win32.ShowWindow(h, Win32.SW_HIDE);
    }

    private static IEnumerable<IntPtr> TaskbarWindows()
    {
        // Our own tray service also owns a "Shell_TrayWnd" (see Tray), which has to stay untouched.
        foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            var h = IntPtr.Zero;
            while ((h = Native.FindWindowEx(IntPtr.Zero, h, cls, null)) != IntPtr.Zero)
            {
                Win32.GetWindowThreadProcessId(h, out var pid);
                if (pid != (uint)Environment.ProcessId) yield return h;
            }
        }
    }
}
