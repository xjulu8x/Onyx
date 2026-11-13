using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace DesktopOverlay;

public class PinItem
{
    public string Key { get; set; }
    public string Name { get; set; }
    public string LaunchPath { get; set; }
}

public class Settings
{
    public bool AlwaysExpanded { get; set; }
    public bool TransparentTaskbar { get; set; } = true; // only read once to seed TaskbarTransparency for older settings files
    /// <summary>0 = opaque, 1 = fully see-through (the bar is capped so it never disappears completely).</summary>
    public double? TaskbarTransparency { get; set; }
    public bool TaskbarAutoHide { get; set; }
    public bool SaveMemory { get; set; }

    /// <summary>Version of the default layout; older settings are moved to the notch at the top edge once (2.0).</summary>
    public int Layout { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public double Transparency => TaskbarTransparency ?? (TransparentTaskbar ? 0.12 : 0.02);
    public bool ShowWidgets { get; set; } = true;
    public string IslandColor { get; set; } = "#0A0A0A";
    public string TaskbarColor { get; set; } = "#0A0A0A";
    public string WidgetColor { get; set; } = "#E6E6E6";
    public Dictionary<string, double[]> WidgetPositions { get; set; } = new();

    // Design page
    public string AccentColor { get; set; } = "#E6E6E6";
    public string FontFamily { get; set; } = "Segoe UI Variable Display, Segoe UI";
    public int AnimationSpeed { get; set; } = 1; // 0 fast, 1 normal, 2 off
    public double TaskbarHeight { get; set; } = 50;
    public double TaskbarIconSize { get; set; } = 24;
    public double TaskbarRadius { get; set; } = 4;
    public bool TaskbarAppsLeft { get; set; }
    public double IslandScale { get; set; } = 1;
    public double IslandCorners { get; set; } = 0.3; // share of the full rounding
    public double IslandOpacity { get; set; } = 0.97;
    public double WidgetScale { get; set; } = 1;
    public double WidgetOpacity { get; set; } = 1;
    public int TaskbarShape { get; set; } // 0 bar, 1 dock, 2 three islands
    public int IconTint { get; set; } // 0 real colors, 1 white, 2 grey, 3 accent
    public int IndicatorStyle { get; set; } = 1; // 0 line, 1 dot, 2 glow, 3 none
    public int RimStyle { get; set; } = 1; // 0 none, 1 subtle, 2 accent, 3 light run
    public int ClockStyle { get; set; } // 0 time and date, 1 time, 2 large
    public bool Grain { get; set; } = true;
    public int IslandShape { get; set; } = 1; // 0 floating, 1 docked (default since 2.0), 2 capsule
    public int MotionStyle { get; set; } // 0 soft, 1 springy, 2 direct
    public int WidgetGraph { get; set; } // 0 line, 1 bars, 2 rings
    public bool WidgetsFree { get; set; } // every block is its own widget

    // Cards of the terminal look (2.3). Missing entries count as switched on.
    public Dictionary<string, bool> Cards { get; set; } = new();
    public bool CardOn(string key) => !Cards.TryGetValue(key, out var on) || on;
    public string WeatherCity { get; set; } = "";
    public double[] WeatherPlace { get; set; } // latitude, longitude of WeatherCity
    public int TerminalMode { get; set; } = 4; // 0 fetch, 1 cava, 2 life, 3 orb, 4 rain
    public int BonsaiSeed { get; set; }
    public DateTime BonsaiWatered { get; set; }
    public DateTime BonsaiPlanted { get; set; }
    public bool BonsaiWind { get; set; } = true;
    public bool BonsaiWinter { get; set; }
    public int SignalMode { get; set; } // 0 face, 1 pulse, 2 clock

    // Pins of the dock (2.4): Onyx keeps its own list instead of changing the Windows taskbar.
    public List<PinItem> OwnPins { get; set; } = new();
    public List<string> HiddenPins { get; set; } = new(); // Windows pins taken off the dock

    // Time-of-day design and own themes
    public bool AutoTheme { get; set; }
    public string DayTheme { get; set; } = "Mono";
    public string NightTheme { get; set; } = "Mono";
    public Dictionary<string, Dictionary<string, JsonElement>> CustomThemes { get; set; } = new();

    // Effects
    public int MusicGlow { get; set; } // 0 off, 1 accent color, 2 color of the cover
    public bool HeatAlarm { get; set; } = true;
    public double HeatLimit { get; set; } = 90;
    public Dictionary<string, string> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase); // program file -> theme

    /// <summary>Parses a "#RRGGBB" setting; falls back to the given color when the value is broken.</summary>
    public static System.Windows.Media.Color ToColor(string hex, byte alpha, System.Windows.Media.Color fallback)
    {
        try { var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex); c.A = alpha; return c; }
        catch { fallback.A = alpha; return fallback; }
    }

    private const string AppName = "Onyx";
    // Earlier names (newest first); their settings and autostart entries are migrated.
    private static readonly string[] OldNames = { "Dynamic Island", "DesktopOverlay" };

    private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string FilePath = Path.Combine(AppData, AppName, "settings.json");
    private static readonly object SaveLock = new();

    public static Settings Current { get; } = Load();

    private static Settings Load()
    {
        try
        {
            var path = new[] { FilePath }.Concat(OldNames.Select(n => Path.Combine(AppData, n, "settings.json"))).FirstOrDefault(File.Exists);
            if (path == null) return new();
            _loadedFromOldName = path != FilePath;
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new();
        }
        catch { return new(); }
    }

    /// <summary>Writes to a temp file first and swaps it in, so a crash or power loss never leaves a half-written file.</summary>
    public void Save() => Theme.RestoreForSave(Write);

    private void Write()
    {
        lock (SaveLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch { }
        }
    }

    private static bool _loadedFromOldName;

    /// <summary>
    /// Runs once per start in the background: moves settings and autostart over from an earlier app name, and points
    /// an existing autostart entry to this EXE if it was moved or renamed.
    /// </summary>
    public static void MigrateAndRepair()
    {
        if (_loadedFromOldName)
        {
            _loadedFromOldName = false;
            bool hadAutostart = OldNames.Any(n => Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(n) != null || Schtasks($"/Query /TN \"{n}\"").Exit == 0);
            if (hadAutostart) Autostart = true;
            Current.Save();
        }
        var target = AutostartTarget();
        if (target != null && !string.Equals(target, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) Autostart = true;
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Starts the overlay at logon. When running as admin a scheduled task with highest privileges is used,
    /// so the CPU temperature sensor keeps working without a UAC prompt; otherwise the normal Run key.
    /// </summary>
    public static bool Autostart
    {
        get => AutostartTarget() != null;
        set
        {
            // Clear entries under the current and all earlier names.
            foreach (var name in OldNames.Prepend(AppName))
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)!) k.DeleteValue(name, false);
                Schtasks($"/Delete /TN \"{name}\" /F");
            }
            if (!value) return;
            if (!SystemMonitor.IsElevated || !CreateLogonTask())
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)!) k.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
        }
    }

    /// <summary>Path the autostart entry launches, or null when autostart is off.</summary>
    private static string AutostartTarget()
    {
        if (Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(AppName) is string run) return run.Trim('"');
        var (exit, output) = Schtasks($"/Query /TN \"{AppName}\" /XML");
        if (exit != 0) return null;
        var m = System.Text.RegularExpressions.Regex.Match(output, "<Command>(.*?)</Command>");
        return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim('"') : "";
    }

    /// <summary>
    /// Logon task with admin rights. Created from XML because the schtasks defaults would keep it from starting on
    /// battery power and end the app after 72 hours.
    /// </summary>
    private static bool CreateLogonTask()
    {
        string Esc(string s) => System.Security.SecurityElement.Escape(s);
        var user = Esc(System.Security.Principal.WindowsIdentity.GetCurrent().Name);
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Startet {AppName} bei der Anmeldung.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <StartWhenAvailable>true</StartWhenAvailable>
                <Priority>5</Priority>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author"><Exec><Command>{Esc(Environment.ProcessPath)}</Command></Exec></Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), "onyx_task.xml");
        try
        {
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
            return Schtasks($"/Create /TN \"{AppName}\" /XML \"{file}\" /F").Exit == 0;
        }
        catch { return false; }
        finally { try { File.Delete(file); } catch { } }
    }

    private static (int Exit, string Output) Schtasks(string args)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("schtasks.exe", args)
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            var output = p.StandardOutput.ReadToEnd(); // read before waiting, so a large output can't block the process
            p.WaitForExit(5000);
            return (p.ExitCode, output);
        }
        catch { return (-1, ""); }
    }
}
