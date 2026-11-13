using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopOverlay;

internal static class Win32
{
    public const int SW_HIDE = 0, SW_SHOWNA = 8, SW_RESTORE = 9, SW_MINIMIZE = 6;
    public const int GW_OWNER = 4, WS_EX_APPWINDOW = 0x40000, DWMWA_CLOAKED = 14;
    public const uint WM_CLOSE = 0x10, WM_GETICON = 0x7F;
    public const byte VK_LWIN = 0x5B, VK_MENU = 0x12;

    public delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] public static extern IntPtr GetClassLongPtr(IntPtr h, int idx);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, int[] ids, uint count, uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr p, int flags, StringBuilder sb, ref int size);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO { public IntPtr hIcon; public int iIcon; public uint attr; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string name; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string type; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO info, uint size, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public Native.RECT rc; public IntPtr lParam; }
    [DllImport("shell32.dll")] public static extern uint SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Native.POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);

    /// <summary>Presses a key combination, e.g. Win+A.</summary>
    public static void Keys(params byte[] keys)
    {
        foreach (var k in keys) keybd_event(k, 0, 0, UIntPtr.Zero);
        foreach (var k in keys.Reverse()) keybd_event(k, 0, 2, UIntPtr.Zero);
    }

    public static void Activate(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        if (!SetForegroundWindow(h))
        {
            // Alt press lifts the foreground lock.
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            SetForegroundWindow(h);
            keybd_event(VK_MENU, 0, 2, UIntPtr.Zero);
        }
    }
}

public class AppEntry
{
    public string Key;           // lower-case exe path, or the .lnk path for shortcuts without a file target
    public string Name;
    public string LaunchPath;    // what to start when no window is open
    public bool Pinned;
    public ImageSource Icon;
    public List<IntPtr> Windows = new();
}

/// <summary>Builds the list of pinned shortcuts plus running top-level windows, grouped by executable.</summary>
public static class TaskbarApps
{
    private static readonly string PinnedDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
    private const string TaskbandKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband";
    private static readonly Dictionary<string, ImageSource> IconCache = new();
    private static readonly uint OwnPid = (uint)Environment.ProcessId;
    private static readonly List<string> _openOrder = new();
    private static Dictionary<string, string> _appsFolder; // AUMID -> display name
    private static List<AppEntry> _pinned;
    private static string _pinnedStamp;

    public static List<AppEntry> Snapshot()
    {
        // Only re-read the pins when Windows actually changed them (registry blob or pin folder), not on every poll.
        var stamp = PinnedStamp();
        if (_pinned == null || stamp != _pinnedStamp) { _pinned = ReadPinned(); _pinnedStamp = stamp; }

        var s = Settings.Current;
        var result = _pinned.Where(p => !s.HiddenPins.Contains(p.Key))
            .Select(p => new AppEntry { Key = p.Key, Name = p.Name, LaunchPath = p.LaunchPath, Pinned = true, Icon = p.Icon }).ToList();
        foreach (var own in s.OwnPins.Where(o => result.All(r => r.Key != o.Key)))
            result.Add(new AppEntry { Key = own.Key, Name = own.Name, LaunchPath = own.LaunchPath, Pinned = true, Icon = PinIcon(own) });
        var byKey = result.ToDictionary(e => e.Key);

        foreach (var h in TopWindows())
        {
            var pid = ProcessOf(h);
            if (pid == 0) continue;
            var exe = PathOf(pid);
            if (exe == null) continue;
            // Store / MSIX apps are identified by their app id, so they merge with their pinned icon.
            var aumid = AumidOf(pid);
            var key = aumid?.ToLowerInvariant() ?? exe.ToLowerInvariant();
            if (!byKey.TryGetValue(key, out var entry))
            {
                entry = new AppEntry { Key = key };
                if (aumid != null)
                {
                    entry.LaunchPath = @"shell:AppsFolder\" + aumid;
                    entry.Name = AppsFolder().GetValueOrDefault(aumid) ?? Path.GetFileNameWithoutExtension(exe);
                    entry.Icon = AppIcon(aumid) ?? IconFor(exe) ?? WindowIcon(h);
                }
                else
                {
                    entry.LaunchPath = exe;
                    entry.Name = FileVersionInfo.GetVersionInfo(exe).FileDescription is { Length: > 0 } d ? d : Path.GetFileNameWithoutExtension(exe);
                    entry.Icon = IconFor(exe) ?? WindowIcon(h);
                }
                byKey[key] = entry;
                result.Add(entry);
            }
            entry.Windows.Add(h);
        }

        // Window enumeration follows z-order, which changes on every focus switch. Keep unpinned apps in the
        // order they first appeared instead, so icons never jump around.
        var running = result.Where(e => !e.Pinned).ToList();
        _openOrder.RemoveAll(k => running.All(e => e.Key != k));
        foreach (var e in running) if (!_openOrder.Contains(e.Key)) _openOrder.Add(e.Key);
        return result.Where(e => e.Pinned).Concat(running.OrderBy(e => _openOrder.IndexOf(e.Key))).ToList();
    }

    public static List<AppEntry> Pinned() => ReadPinned();

    /// <summary>Puts an app on the dock. Windows pins that were taken off come back; anything else goes on Onyx's own list.</summary>
    public static void Pin(AppEntry app)
    {
        var s = Settings.Current;
        if (!s.HiddenPins.Remove(app.Key) && s.OwnPins.All(p => p.Key != app.Key))
            s.OwnPins.Add(new PinItem { Key = app.Key, Name = app.Name, LaunchPath = app.LaunchPath });
        s.Save();
    }

    /// <summary>Takes an app off the dock. Windows itself is not changed: its pins are only hidden here.</summary>
    public static void Unpin(AppEntry app)
    {
        var s = Settings.Current;
        if (s.OwnPins.RemoveAll(p => p.Key == app.Key) == 0 && !s.HiddenPins.Contains(app.Key)) s.HiddenPins.Add(app.Key);
        s.Save();
    }

    private static ImageSource PinIcon(PinItem pin)
    {
        if (pin.LaunchPath.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase)) return AppIcon(pin.LaunchPath[16..]);
        return File.Exists(pin.LaunchPath) ? IconFor(pin.LaunchPath) ?? ShellIcon(pin.LaunchPath) : null;
    }

    private static string PinnedStamp()
    {
        var blob = Registry.CurrentUser.OpenSubKey(TaskbandKey)?.GetValue("Favorites") as byte[];
        var dirTime = Directory.Exists(PinnedDir) ? Directory.GetLastWriteTimeUtc(PinnedDir).Ticks : 0;
        return $"{blob?.Length}:{(blob == null ? 0 : blob.Aggregate(17, (h, b) => h * 31 + b))}:{dirTime}";
    }

    /// <summary>
    /// Pins come from two places: .lnk files for desktop apps, and the Taskband "Favorites" blob, which is the only
    /// place Store/MSIX pins (Settings, Firefox from the Store, ...) are recorded. The blob also holds the real order.
    /// </summary>
    private static List<AppEntry> ReadPinned()
    {
        var blob = Registry.CurrentUser.OpenSubKey(TaskbandKey)?.GetValue("Favorites") as byte[] ?? Array.Empty<byte>();
        // The blob is a list of shell item IDs; names sit in it as UTF-16 at either byte alignment.
        var even = Encoding.Unicode.GetString(blob);
        var odd = blob.Length > 1 ? Encoding.Unicode.GetString(blob, 1, blob.Length - 1) : "";
        int Position(string text)
        {
            int a = even.IndexOf(text, StringComparison.OrdinalIgnoreCase), b = odd.IndexOf(text, StringComparison.OrdinalIgnoreCase);
            if (a < 0 && b < 0) return int.MaxValue;
            return Math.Min(a < 0 ? int.MaxValue : a * 2, b < 0 ? int.MaxValue : b * 2 + 1);
        }

        var found = new List<(int Pos, AppEntry App)>();

        if (Directory.Exists(PinnedDir))
        {
            dynamic shell = null;
            try { shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!); } catch { }
            foreach (var lnk in new DirectoryInfo(PinnedDir).GetFiles("*.lnk"))
            {
                string target = "", iconLoc = "";
                try { var sc = shell?.CreateShortcut(lnk.FullName); target = sc?.TargetPath ?? ""; iconLoc = sc?.IconLocation ?? ""; } catch { }
                target = Environment.ExpandEnvironmentVariables(target);
                bool fileTarget = target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target);
                var key = fileTarget ? target.ToLowerInvariant() : lnk.FullName.ToLowerInvariant();
                if (found.Any(e => e.App.Key == key)) continue;

                ImageSource icon = null;
                var loc = Environment.ExpandEnvironmentVariables(iconLoc);
                var comma = loc.LastIndexOf(',');
                if (comma > 0 && File.Exists(loc[..comma])) icon = IconFor(loc[..comma], int.TryParse(loc[(comma + 1)..], out var i) ? i : 0);
                icon ??= fileTarget ? IconFor(target) : null;
                icon ??= ShellIcon(lnk.FullName);

                int pos = Position(lnk.Name);
                if (pos == int.MaxValue) pos = int.MaxValue - 1000 + found.Count; // unknown order: append
                found.Add((pos, new AppEntry { Key = key, Name = Path.GetFileNameWithoutExtension(lnk.Name), LaunchPath = lnk.FullName, Pinned = true, Icon = icon }));
            }
        }

        // Package identities in the blob: "Name_publisherid[!AppId]" or full "Name_1.2.3.0_x64__publisherid".
        var families = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rx = new Regex(@"([A-Za-z][A-Za-z0-9.\-]+)_(?:[\d.]+_[A-Za-z0-9]+_[^_\\]*_)?([a-z0-9]{13})(?![a-z0-9])");
        foreach (var (text, offset) in new[] { (even, 0), (odd, 1) })
            foreach (Match m in rx.Matches(text))
            {
                var family = m.Groups[1].Value + "_" + m.Groups[2].Value;
                int pos = m.Index * 2 + offset;
                if (!families.TryGetValue(family, out var p) || pos < p) families[family] = pos;
            }
        foreach (var (family, pos) in families)
        {
            var aumid = AppsFolder().Keys.FirstOrDefault(k => k.StartsWith(family + "!", StringComparison.OrdinalIgnoreCase));
            if (aumid == null || found.Any(e => e.App.Key == aumid.ToLowerInvariant())) continue;
            found.Add((pos, new AppEntry
            {
                Key = aumid.ToLowerInvariant(), Name = AppsFolder()[aumid], LaunchPath = @"shell:AppsFolder\" + aumid, Pinned = true, Icon = AppIcon(aumid),
            }));
        }

        return found.OrderBy(f => f.Pos).Select(f => f.App).ToList();
    }

    /// <summary>All installed apps (shell:AppsFolder): app id -> display name. Read once.</summary>
    private static Dictionary<string, string> AppsFolder()
    {
        if (_appsFolder != null) return _appsFolder;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            foreach (var item in shell.NameSpace("shell:AppsFolder").Items())
            {
                string path = item.Path, name = item.Name;
                if (path != null && path.Contains('!')) map[path] = name;
            }
        }
        catch { }
        return _appsFolder = map;
    }

    /// <summary>
    /// Every app Windows lists in Start (shell:AppsFolder): Store apps, desktop programs and games, also those without a
    /// Start-menu shortcut. Each id can be launched as "shell:AppsFolder\" + id. Read fresh on every call (STA thread).
    /// </summary>
    internal static List<(string Id, string Name)> AllApps()
    {
        var list = new List<(string, string)>();
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            foreach (var item in shell.NameSpace("shell:AppsFolder").Items())
            {
                string id = item.Path, name = item.Name;
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name)) list.Add((id, name));
            }
        }
        catch { }
        return list;
    }

    private static readonly Dictionary<(ImageSource, Color), ImageSource> Tints = new();

    /// <summary>The icon in a single color: shape and shading stay, the hue goes. Results are kept, so this runs once per icon.</summary>
    internal static ImageSource Tinted(ImageSource icon, Color color)
    {
        if (icon is not BitmapSource source) return icon;
        if (Tints.TryGetValue((icon, color), out var known)) return known;
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        var px = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(px, stride, 0);
        for (int i = 0; i < px.Length; i += 4)
        {
            double light = 0.55 + 0.45 * (0.114 * px[i] + 0.587 * px[i + 1] + 0.299 * px[i + 2]) / 255;
            px[i] = (byte)(color.B * light);
            px[i + 1] = (byte)(color.G * light);
            px[i + 2] = (byte)(color.R * light);
        }
        var result = BitmapSource.Create(bgra.PixelWidth, bgra.PixelHeight, source.DpiX, source.DpiY, PixelFormats.Bgra32, null, px, stride);
        result.Freeze();
        return Tints[(icon, color)] = result;
    }

    internal static uint ProcessOf(IntPtr h)
    {
        Win32.GetWindowThreadProcessId(h, out var pid);
        if (pid == OwnPid) return 0;
        // UWP apps live inside ApplicationFrameHost; the real process owns a child CoreWindow.
        if (PathOf(pid)?.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) == true)
        {
            uint host = pid;
            Win32.EnumChildWindows(h, (c, _) =>
            {
                Win32.GetWindowThreadProcessId(c, out var cp);
                if (cp != host) { pid = cp; return false; }
                return true;
            }, IntPtr.Zero);
        }
        return pid;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder id);

    private static string AumidOf(uint pid)
    {
        var p = Win32.OpenProcess(0x1000, false, pid);
        if (p == IntPtr.Zero) return null;
        try
        {
            uint len = 256;
            var sb = new StringBuilder((int)len);
            return GetApplicationUserModelId(p, ref len, sb) == 0 ? sb.ToString() : null;
        }
        finally { Win32.CloseHandle(p); }
    }

    // ---- Store app icons (shell item image factory) ----

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap); }

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, out BITMAP bmp);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);

    /// <summary>Icon of an installed app by its app id (works for Store apps, which have no .exe icon).</summary>
    internal static ImageSource AppIcon(string aumid, int size = 48)
    {
        var cacheKey = "aumid:" + aumid.ToLowerInvariant() + "," + size;
        lock (IconCache) if (IconCache.TryGetValue(cacheKey, out var cached)) return cached;
        ImageSource img = null;
        IntPtr hbmp = IntPtr.Zero, dc = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(@"shell:AppsFolder\" + aumid, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            if (factory.GetImage(new SIZE { cx = size, cy = size }, 0x4 /* ICONONLY */, out hbmp) == 0 && GetObject(hbmp, Marshal.SizeOf<BITMAP>(), out var bm) != 0)
            {
                int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
                // Ask for a top-down 32-bit copy so orientation and alpha are well defined.
                var info = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                var px = new byte[w * h * 4];
                dc = GetDC(IntPtr.Zero);
                if (GetDIBits(dc, hbmp, 0, (uint)h, px, ref info, 0) > 0)
                {
                    var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
                    src.Freeze();
                    img = src;
                }
            }
        }
        catch { }
        finally
        {
            if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
            if (dc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, dc);
        }
        lock (IconCache) IconCache[cacheKey] = img;
        return img;
    }

    private static IEnumerable<IntPtr> TopWindows()
    {
        var list = new List<IntPtr>();
        Win32.EnumWindows((h, _) =>
        {
            if (IsAppWindow(h)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static bool IsAppWindow(IntPtr h)
    {
        if (!Win32.IsWindowVisible(h) || Win32.GetWindow(h, Win32.GW_OWNER) != IntPtr.Zero) return false;
        int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Win32.WS_EX_APPWINDOW) == 0) return false;
        if (Win32.GetWindowTextLength(h) == 0) return false;
        if (Win32.DwmGetWindowAttribute(h, Win32.DWMWA_CLOAKED, out var cloaked, 4) == 0 && cloaked != 0) return false;
        var cls = new StringBuilder(64);
        Win32.GetClassName(h, cls, 64);
        return cls.ToString() is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }

    internal static string PathOf(uint pid)
    {
        var p = Win32.OpenProcess(0x1000, false, pid);
        if (p == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return Win32.QueryFullProcessImageName(p, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { Win32.CloseHandle(p); }
    }

    internal static ImageSource IconFor(string file, int index = 0, int size = 48)
    {
        var cacheKey = file.ToLowerInvariant() + "," + index + "," + size;
        lock (IconCache) if (IconCache.TryGetValue(cacheKey, out var cached)) return cached;
        ImageSource img = null;
        var icons = new IntPtr[1];
        if (Win32.PrivateExtractIcons(file, index, size, size, icons, new int[1], 1, 0) > 0 && icons[0] != IntPtr.Zero)
        {
            img = FromHIcon(icons[0]);
            Win32.DestroyIcon(icons[0]);
        }
        lock (IconCache) IconCache[cacheKey] = img;
        return img;
    }

    internal static ImageSource ShellIcon(string path)
    {
        var info = new Win32.SHFILEINFO();
        Win32.SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), 0x100 /* SHGFI_ICON | LARGEICON */);
        if (info.hIcon == IntPtr.Zero) return null;
        var img = FromHIcon(info.hIcon);
        Win32.DestroyIcon(info.hIcon);
        return img;
    }

    private static ImageSource WindowIcon(IntPtr h)
    {
        Win32.SendMessageTimeout(h, Win32.WM_GETICON, (IntPtr)1, IntPtr.Zero, 2, 100, out var icon);
        if (icon == IntPtr.Zero) icon = Win32.GetClassLongPtr(h, -14);
        return icon == IntPtr.Zero ? null : FromHIcon(icon);
    }

    private static ImageSource FromHIcon(IntPtr h)
    {
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
    }
}
