using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DesktopOverlay;

/// <summary>One thing that is going on right now, shown live in the notch.</summary>
public sealed record Activity(string Key, string Text, double? Progress, int Priority);

/// <summary>
/// Live activities, like on a phone: browser downloads (from the files they write), Steam downloads (with the real
/// progress from Steam's own records), an app using the microphone (a call: Windows notes which app uses it), and the
/// island's timer. Checked once a second, cheaply; nothing is sent anywhere.
/// </summary>
internal static class LiveActivities
{
    public static event Action Changed;
    public static IReadOnlyList<Activity> Current { get; private set; } = Array.Empty<Activity>();

    private static DispatcherTimer _timer;
    private static int _tick;
    private static readonly Dictionary<string, (long Size, DateTime Seen, DateTime Grew, double Speed)> Files = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (long Done, DateTime At, double Speed)> SteamApps = new();
    private static List<Activity> _browser = new(), _steam = new(), _mic = new();
    private static DateTime? _timerEnd;
    private static TimeSpan _timerLength;

    public static void Start()
    {
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), System.Windows.Application.Current.Dispatcher);
        Tick();
    }

    // ---------- timer ----------

    public static bool TimerRunning => _timerEnd != null;

    public static void StartTimer(TimeSpan length)
    {
        _timerLength = length;
        _timerEnd = DateTime.Now + length;
        Tick();
    }

    public static void StopTimer() { _timerEnd = null; Tick(); }

    /// <summary>Raised once when the timer reaches zero.</summary>
    public static event Action TimerDone;

    private static void Tick()
    {
        _tick++;
        if (_tick % 2 == 0) _browser = Browser();
        if (_tick % 3 == 0) _steam = Steam();
        if (_tick % 2 == 1) _mic = Microphone();

        var list = new List<Activity>();
        if (_timerEnd is { } end)
        {
            var left = end - DateTime.Now;
            if (left <= TimeSpan.Zero) { _timerEnd = null; TimerDone?.Invoke(); }
            else list.Add(new Activity("timer", $"TIMER {(int)left.TotalMinutes:00}:{left.Seconds:00}", 1 - left.TotalSeconds / _timerLength.TotalSeconds, 3));
        }
        list.AddRange(_mic);
        list.AddRange(_steam);
        list.AddRange(_browser);
        list.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        bool changed = list.Count != Current.Count || list.Zip(Current).Any(p => p.First != p.Second);
        Current = list;
        if (changed) Changed?.Invoke();
    }

    private static string Size(double bytes) => bytes >= 1e9 ? $"{bytes / 1e9:0.0} GB" : $"{bytes / 1e6:0} MB";

    // ---------- browser downloads: files that are still being written ----------

    private static string _downloads;

    private static string DownloadsFolder()
    {
        if (_downloads != null) return _downloads;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            var path = key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") as string;
            _downloads = Environment.ExpandEnvironmentVariables(path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        }
        catch { _downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"); }
        return _downloads;
    }

    private static List<Activity> Browser()
    {
        var result = new List<Activity>();
        var now = DateTime.Now;
        try
        {
            var dir = new DirectoryInfo(DownloadsFolder());
            if (!dir.Exists) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in dir.EnumerateFiles().Where(f => f.Extension is ".crdownload" or ".part" or ".partial" or ".download" or ".opdownload"))
            {
                seen.Add(file.FullName);
                long size = file.Length;
                if (!Files.TryGetValue(file.FullName, out var old)) { Files[file.FullName] = (size, now, now, 0); continue; }
                double seconds = Math.Max(0.5, (now - old.Seen).TotalSeconds);
                double speed = size > old.Size ? (size - old.Size) / seconds : 0;
                speed = old.Speed * 0.6 + speed * 0.4;
                var grew = size > old.Size ? now : old.Grew;
                Files[file.FullName] = (size, now, grew, speed);
                if ((now - grew).TotalSeconds > 20) continue; // stalled or left over: not shown
                string name = Path.GetFileNameWithoutExtension(file.Name);
                if (name.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase) || name.Length == 0) name = "Download";
                if (name.Length > 18) name = name[..17] + "…";
                result.Add(new Activity("dl:" + file.FullName, $"↓ {name.ToUpperInvariant()}  {Size(size)} · {Size(speed)}/S", null, 1));
            }
            foreach (var gone in Files.Keys.Where(k => !seen.Contains(k)).ToList()) Files.Remove(gone);
        }
        catch { }
        return result.Take(2).ToList();
    }

    // ---------- Steam: the app manifest says how much of an update or game is downloaded ----------

    private static List<string> _libraries;

    private static List<string> Libraries()
    {
        if (_libraries != null) return _libraries;
        _libraries = new List<string>();
        try
        {
            using var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (steam?.GetValue("SteamPath") is not string root) return _libraries;
            root = root.Replace('/', '\\');
            var file = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            _libraries.Add(Path.Combine(root, "steamapps"));
            if (File.Exists(file))
                foreach (Match m in Regex.Matches(File.ReadAllText(file), "\"path\"\\s*\"([^\"]+)\""))
                {
                    var lib = Path.Combine(m.Groups[1].Value.Replace("\\\\", "\\"), "steamapps");
                    if (!_libraries.Contains(lib, StringComparer.OrdinalIgnoreCase)) _libraries.Add(lib);
                }
        }
        catch { }
        return _libraries;
    }

    private static List<Activity> Steam()
    {
        var result = new List<Activity>();
        var now = DateTime.Now;
        foreach (var lib in Libraries())
        {
            try
            {
                var downloading = Path.Combine(lib, "downloading");
                if (!Directory.Exists(downloading) || !Directory.EnumerateFileSystemEntries(downloading).Any()) continue;
                foreach (var manifest in Directory.EnumerateFiles(lib, "appmanifest_*.acf"))
                {
                    string text = File.ReadAllText(manifest);
                    long Number(string key) { var m = Regex.Match(text, $"\"{key}\"\\s*\"(\\d+)\""); return m.Success ? long.Parse(m.Groups[1].Value) : 0; }
                    long total = Number("BytesToDownload"), done = Number("BytesDownloaded");
                    if (total <= 0 || done >= total) continue;
                    string name = Regex.Match(text, "\"name\"\\s*\"([^\"]+)\"").Groups[1].Value;
                    if (name.Length > 16) name = name[..15] + "…";
                    string id = Path.GetFileName(manifest);
                    double speed = 0;
                    if (SteamApps.TryGetValue(id, out var old) && done > old.Done) speed = old.Speed * 0.5 + (done - old.Done) / Math.Max(1, (now - old.At).TotalSeconds) * 0.5;
                    else if (SteamApps.TryGetValue(id, out var same)) speed = same.Speed * 0.7;
                    SteamApps[id] = (done, now, speed);
                    string rate = speed > 1000 ? $" · {Size(speed)}/S" : " · PAUSE";
                    result.Add(new Activity("steam:" + id, $"STEAM {name.ToUpperInvariant()} {done * 100 / total}%{rate}", done / (double)total, 2));
                }
            }
            catch { }
        }
        return result.Take(1).ToList();
    }

    // ---------- microphone: Windows records which app uses it, so a call shows up ----------

    private static List<Activity> Microphone()
    {
        var result = new List<Activity>();
        try
        {
            using var store = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone");
            if (store == null) return result;
            void Check(RegistryKey parent, string name, bool path)
            {
                using var app = parent.OpenSubKey(name);
                if (app?.GetValue("LastUsedTimeStop") is not long stop || stop != 0) return;
                if (app.GetValue("LastUsedTimeStart") is not long start || start == 0) return;
                string label = path ? Path.GetFileNameWithoutExtension(name.Replace('#', '\\')) : name.Split('_')[0].Split('.').Last();
                if (label.Equals("Onyx", StringComparison.OrdinalIgnoreCase)) return;
                label = label switch { "Discord" or "DiscordPTB" or "DiscordCanary" => "Discord", "ms-teams" or "Teams" => "Teams", "Zoom" => "Zoom", _ => char.ToUpper(label[0]) + label[1..] };
                var since = DateTime.Now - DateTime.FromFileTime(start);
                // Minutes only, so the notch text changes rarely.
                string time = since.TotalMinutes < 1 ? "JETZT" : since.TotalHours >= 1 ? $"{(int)since.TotalHours} H {since.Minutes} MIN" : $"{(int)since.TotalMinutes} MIN";
                result.Add(new Activity("mic:" + label, $"● {label.ToUpperInvariant()}  {time}", null, 4));
            }
            using (var nonPackaged = store.OpenSubKey("NonPackaged"))
                if (nonPackaged != null) foreach (var name in nonPackaged.GetSubKeyNames()) Check(nonPackaged, name, true);
            foreach (var name in store.GetSubKeyNames().Where(n => n != "NonPackaged")) Check(store, name, false);
        }
        catch { }
        return result.Take(1).ToList();
    }
}
