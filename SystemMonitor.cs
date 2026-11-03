using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DesktopOverlay;

public record DiskInfo(string Name, double FreeGB, double TotalGB)
{
    public double UsedFraction => TotalGB <= 0 ? 0 : 1 - FreeGB / TotalGB;
}

/// <summary>Polls system stats once per second on the UI thread and raises <see cref="Updated"/>.</summary>
public class SystemMonitor
{
    public static SystemMonitor Instance { get; } = new();

    public event Action Updated;

    public double Cpu { get; private set; }
    public List<double> CpuHistory { get; } = new();
    public double RamUsedGB { get; private set; }
    public double RamTotalGB { get; private set; }
    public double DownBps { get; private set; }
    public double UpBps { get; private set; }
    public double? PingMs { get; private set; }
    public string LocalIp { get; private set; } = "-";
    public List<DiskInfo> Disks { get; private set; } = new();
    public long BinBytes { get; private set; }
    public long BinItems { get; private set; }
    public double? CpuTemp { get; private set; }
    public double? GpuTemp { get; private set; }
    public string CpuVendor { get; private set; } = "";
    public string CpuModel { get; private set; } = "";

    private long _idle, _kernel, _user;
    private long _rx, _tx;
    private DateTime _lastNet;
    private int _tick;
    private string _nvidiaSmi;
    private LibreHardwareMonitor.Hardware.Computer _hw;

    private SystemMonitor()
    {
        ReadCpuName();
        new Thread(SensorLoop) { IsBackground = true, Name = "Sensors" }.Start();
        _nvidiaSmi = new[] { @"C:\Windows\System32\nvidia-smi.exe" }.FirstOrDefault(File.Exists);
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) => Tick();
        t.Start();
        Tick();
    }

    private void Tick()
    {
        try { SampleCpu(); } catch { }
        try { SampleRam(); } catch { }
        try { SampleNet(); } catch { }
        if (_tick % 3 == 0) _ = SamplePing();
        if (_tick % 5 == 0)
        {
            try { SampleDisks(); } catch { }
            try { SampleBin(); } catch { }
        }
        _tick++;
        Updated?.Invoke();
    }

    private void SampleCpu()
    {
        Native.GetSystemTimes(out var idle, out var kernel, out var user);
        long di = idle - _idle, dk = kernel - _kernel, du = user - _user;
        _idle = idle; _kernel = kernel; _user = user;
        long total = dk + du;
        if (total > 0 && _tick > 0) Cpu = Math.Clamp(100.0 * (total - di) / total, 0, 100);
        CpuHistory.Add(Cpu);
        if (CpuHistory.Count > 90) CpuHistory.RemoveAt(0);
    }

    private void SampleRam()
    {
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        Native.GlobalMemoryStatusEx(ref m);
        RamTotalGB = m.ullTotalPhys / 1073741824.0;
        RamUsedGB = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
    }

    private void SampleNet()
    {
        long rx = 0, tx = 0;
        string local = null;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var s = ni.GetIPStatistics();
            rx += s.BytesReceived; tx += s.BytesSent;
            var props = ni.GetIPProperties();
            if (local == null && props.GatewayAddresses.Count > 0)
                local = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
        }
        var now = DateTime.UtcNow;
        if (_lastNet != default)
        {
            var dt = (now - _lastNet).TotalSeconds;
            if (dt > 0) { DownBps = Math.Max(0, (rx - _rx) / dt); UpBps = Math.Max(0, (tx - _tx) / dt); }
        }
        _rx = rx; _tx = tx; _lastNet = now;
        LocalIp = local ?? "-";
    }

    private async Task SamplePing()
    {
        try
        {
            using var p = new Ping();
            var r = await p.SendPingAsync("1.1.1.1", 1000);
            PingMs = r.Status == IPStatus.Success ? r.RoundtripTime : null;
        }
        catch { PingMs = null; }
    }

    private void SampleDisks()
    {
        Disks = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => new DiskInfo(d.Name.Replace('\\', '/'), d.TotalFreeSpace / 1073741824.0, d.TotalSize / 1073741824.0))
            .ToList();
    }

    private void SampleBin()
    {
        var info = new Native.SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.SHQUERYRBINFO>() };
        if (Native.SHQueryRecycleBin(null, ref info) == 0) { BinBytes = info.i64Size; BinItems = info.i64NumItems; }
    }

    public static bool IsElevated =>
        new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    /// <summary>LibreHardwareMonitor reads CPU sensors through the PawnIO kernel driver; without it the CPU temperature stays empty.</summary>
    public static bool DriverInstalled
    {
        get { using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO"); return k != null; }
    }

    /// <summary>Why the CPU temperature is missing, or null when nothing is known to be missing.</summary>
    public static string CpuTempProblem => !IsElevated ? "Admin nötig" : !DriverInstalled ? "Treiber fehlt" : null;

    /// <summary>Installs PawnIO via winget (signed setup from its official source). Falls back to the download page.</summary>
    public static async Task<bool> InstallDriver()
    {
        bool ok = await Task.Run(() =>
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("winget", "install --id namazso.PawnIO -e --silent --accept-package-agreements --accept-source-agreements")
                { UseShellExecute = false, CreateNoWindow = true })!;
                p.WaitForExit(180000);
            }
            catch { }
            return DriverInstalled;
        });
        if (!ok) try { Process.Start(new ProcessStartInfo("https://pawnio.eu/") { UseShellExecute = true }); } catch { }
        return ok;
    }

    private void OpenSensors()
    {
        try { _hw?.Close(); } catch { }
        try
        {
            _hw = new LibreHardwareMonitor.Hardware.Computer { IsCpuEnabled = true, IsGpuEnabled = true };
            _hw.Open();
        }
        catch { _hw = null; }
    }

    /// <summary>Reads CPU/GPU temperatures every 2 s on its own thread (LibreHardwareMonitor; CPU needs admin rights).</summary>
    private void SensorLoop()
    {
        bool openedWithDriver = DriverInstalled;
        OpenSensors();

        while (true)
        {
            // Driver installed while running: reopen so the CPU sensors appear without a restart.
            if (!openedWithDriver && CpuTemp == null && DriverInstalled) { openedWithDriver = true; OpenSensors(); }
            double? cpu = null, gpu = null;
            try
            {
                foreach (var h in _hw?.Hardware ?? (IList<LibreHardwareMonitor.Hardware.IHardware>)Array.Empty<LibreHardwareMonitor.Hardware.IHardware>())
                {
                    h.Update();
                    var temps = h.Sensors.Where(s => s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Temperature && s.Value is > 0 and < 150).ToList();
                    if (temps.Count == 0) continue;
                    if (h.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Cpu)
                        cpu ??= Pick(temps, "Core (Tctl/Tdie)", "CPU Package", "Core (Tctl)", "Tdie", "Core Average");
                    else if (h.HardwareType is LibreHardwareMonitor.Hardware.HardwareType.GpuNvidia or LibreHardwareMonitor.Hardware.HardwareType.GpuAmd or LibreHardwareMonitor.Hardware.HardwareType.GpuIntel)
                        gpu ??= Pick(temps, "GPU Core", "GPU Hot Spot");
                }
            }
            catch { }
            gpu ??= NvidiaSmiTemp();
            CpuTemp = cpu;
            GpuTemp = gpu;
            Thread.Sleep(2000);
        }
    }

    private static double? Pick(List<LibreHardwareMonitor.Hardware.ISensor> temps, params string[] preferred)
    {
        foreach (var name in preferred)
            if (temps.FirstOrDefault(s => s.Name == name) is { } s) return s.Value;
        return temps.Max(s => s.Value);
    }

    private double? NvidiaSmiTemp()
    {
        if (_nvidiaSmi == null) return null;
        try
        {
            var psi = new ProcessStartInfo(_nvidiaSmi, "--query-gpu=temperature.gpu --format=csv,noheader,nounits")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            return double.TryParse(output.Split('\n')[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var g) ? g : null;
        }
        catch { return null; }
    }

    private void ReadCpuName()
    {
        var name = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "") as string ?? "";
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " ").Trim()
            .Replace("(R)", "®").Replace("(TM)", "").Replace(" CPU", "");
        name = System.Text.RegularExpressions.Regex.Replace(name, @"@.*$|\d+-Core Processor|Processor", "").Trim();
        // "Intel® Core i5-8350U" -> "Intel® Core" / "i5-8350U";  "AMD Ryzen 7 5800X" -> "AMD Ryzen 7" / "5800X"
        var parts = name.Split(' ');
        int split = Math.Max(1, parts.Length - 1);
        CpuVendor = string.Join(' ', parts.Take(split));
        CpuModel = string.Join(' ', parts.Skip(split));
    }

    public static string Rate(double bps) => bps switch
    {
        < 1024 => $"{bps:0} B/s",
        < 1024 * 1024 => $"{bps / 1024:0.0} KB/s",
        _ => $"{bps / 1048576:0.0} MB/s",
    };

    public static string Size(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024.0:0} K",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0} M",
        _ => $"{bytes / 1073741824.0:0.0} G",
    };
}
