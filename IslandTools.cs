using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using ZXing;

namespace DesktopOverlay;

/// <summary>
/// The tools in the island. Each reports its result as a short message in the small island. Nothing here runs
/// until a tool is used: the color picker's mouse hook only exists between the click on the tool and the pick.
/// </summary>
internal static class IslandTools
{
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);

    private static IntPtr _hook;
    private static HookProc _proc; // kept alive for the GC
    private static bool _picked;

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    /// <summary>Windows' own area selection; the picture lands in the clipboard.</summary>
    public static void ScreenshotArea() => Open("ms-screenclip:");

    /// <summary>Win+PrintScreen: Windows saves the whole screen under Bilder\Screenshots.</summary>
    public static void ScreenshotFull(Action<string> report)
    {
        Win32.Keys(0x5B, 0x2C);
        report("Screenshot gespeichert");
    }

    /// <summary>Win+Alt+R starts and stops the Xbox Game Bar recording.</summary>
    public static void Record(Action<string> report)
    {
        Win32.Keys(0x5B, 0x12, 0x52);
        report("Aufnahme gestartet oder beendet");
    }

    /// <summary>Lets the user select an area, then hands the picture on. Gives up after a minute.</summary>
    private static async Task<BitmapSource> PickArea()
    {
        Clipboard.Clear();
        ScreenshotArea();
        for (int i = 0; i < 200; i++)
        {
            await Task.Delay(300);
            if (Clipboard.ContainsImage()) return Clipboard.GetImage();
        }
        return null;
    }

    private static byte[] Pixels(BitmapSource image, out int width, out int height)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        width = bgra.PixelWidth;
        height = bgra.PixelHeight;
        var pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    /// <summary>Reads the text in a selected area with Windows' built-in text recognition and copies it.</summary>
    public static async void ReadText(Action<string> report)
    {
        try
        {
            var image = await PickArea();
            if (image == null) return;
            var pixels = Pixels(image, out int width, out int height);
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null) { report("Keine Texterkennung verfügbar"); return; }
            var text = (await engine.RecognizeAsync(bitmap)).Text;
            if (string.IsNullOrWhiteSpace(text)) { report("Kein Text gefunden"); return; }
            Clipboard.SetText(text);
            report("Text kopiert");
        }
        catch { report("Text nicht lesbar"); }
    }

    /// <summary>Decodes a QR code in a selected area and copies its content.</summary>
    public static async void ReadQr(Action<string> report)
    {
        try
        {
            var image = await PickArea();
            if (image == null) return;
            var pixels = Pixels(image, out int width, out int height);
            var result = new BarcodeReaderGeneric().Decode(new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.BGRA32));
            if (result == null) { report("Kein QR-Code gefunden"); return; }
            Clipboard.SetText(result.Text);
            report("QR-Inhalt kopiert");
        }
        catch { report("QR-Code nicht lesbar"); }
    }

    /// <summary>The next left click takes the color under the cursor instead of reaching the app; right click cancels.</summary>
    public static void PickColor(Action<string> report)
    {
        if (_hook != IntPtr.Zero) return;
        report("Klicke auf eine Farbe");
        _proc = (code, wParam, lParam) =>
        {
            const int WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204;
            if (code >= 0 && wParam == WM_LBUTTONDOWN && !_picked)
            {
                var point = Marshal.PtrToStructure<Native.POINT>(lParam);
                var screen = GetDC(IntPtr.Zero);
                uint pixel = GetPixel(screen, point.X, point.Y);
                ReleaseDC(IntPtr.Zero, screen);
                var hex = $"#{pixel & 0xFF:X2}{(pixel >> 8) & 0xFF:X2}{(pixel >> 16) & 0xFF:X2}";
                _picked = true;
                Application.Current.Dispatcher.BeginInvoke(() => { Clipboard.SetText(hex); report(hex + " kopiert"); });
                return (IntPtr)1;
            }
            // The release of the swallowed click is swallowed too; the hook ends with it.
            if (code >= 0 && wParam == WM_LBUTTONUP && _picked) { Application.Current.Dispatcher.BeginInvoke(Stop); return (IntPtr)1; }
            if (code >= 0 && wParam == WM_RBUTTONDOWN) { Application.Current.Dispatcher.BeginInvoke(() => { Stop(); report("Pipette abgebrochen"); }); return (IntPtr)1; }
            return CallNextHookEx(_hook, code, wParam, lParam);
        };
        using var module = Process.GetCurrentProcess().MainModule!;
        _hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _proc, GetModuleHandle(module.ModuleName), 0);
    }

    private static void Stop()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _picked = false;
    }
}
