using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;

namespace DesktopOverlay;

/// <summary>
/// Looks for a newer GitHub release and, on request, swaps the running EXE for it. The download is checked against the
/// size and SHA-256 GitHub reports; if anything fails the current EXE stays untouched.
/// </summary>
internal static class UpdateService
{
    private const string Api = "https://api.github.com/repos/xjulu8x/Onyx/releases/latest";
    private const string ReleasePage = "https://github.com/xjulu8x/Onyx/releases/latest";

    private sealed record Release(Version Version, string Url, long Size, string Sha256);

    private static Release _latest;
    private static DispatcherTimer _timer;

    /// <summary>The newer version, or null when this one is up to date.</summary>
    public static string Available => _latest == null ? null : _latest.Version.ToString(3);

    /// <summary>Download progress in percent while an update runs, otherwise null.</summary>
    public static int? Progress { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="Available"/> or <see cref="Progress"/> changed.</summary>
    public static event Action Changed;

    private static Version Current => Normalize(Assembly.GetEntryAssembly()!.GetName().Version);
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static string ExePath => Environment.ProcessPath!;
    private static string Sibling(string name) => Path.Combine(Path.GetDirectoryName(ExePath)!, name);

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Onyx-Updater"); // GitHub rejects requests without one
        return http;
    }

    /// <summary>First check shortly after start (the desktop is busy at logon), then every 6 hours.</summary>
    public static void Start()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = TimeSpan.FromHours(6);
            try { File.Delete(Sibling("Onyx.old.exe")); } catch { } // left behind by the last update
            await CheckAsync();
        };
        _timer.Start();
    }

    public static async Task CheckAsync()
    {
        try
        {
            using var http = NewClient();
            using var doc = JsonDocument.Parse(await http.GetStringAsync(Api));
            var root = doc.RootElement;
            if (!Version.TryParse(root.GetProperty("tag_name").GetString()!.TrimStart('v'), out var tag)) return;
            var version = Normalize(tag);

            Release found = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != "Onyx.exe") continue;
                var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
                found = new Release(version, asset.GetProperty("browser_download_url").GetString(), asset.GetProperty("size").GetInt64(),
                    digest?.Replace("sha256:", "").ToLowerInvariant());
            }
            _latest = found != null && version > Current ? found : null;
            Changed?.Invoke();
        }
        catch { } // offline or rate-limited: the next check tries again
    }

    /// <summary>Downloads and verifies the new EXE, then puts it in place of this one. True when the app should restart.</summary>
    public static async Task<bool> InstallAsync(string forcedSha256 = null)
    {
        if (_latest == null || Progress != null) return false;
        var release = _latest;
        var file = Sibling("Onyx.new.exe");
        try
        {
            Report(0);
            await DownloadAsync(release, file);
            var expected = forcedSha256 ?? release.Sha256;
            if (new FileInfo(file).Length != release.Size || string.IsNullOrEmpty(expected) || await HashAsync(file) != expected)
                throw new InvalidDataException("Download does not match the release.");

            // A running EXE can't be overwritten, but it can be renamed.
            var old = Sibling("Onyx.old.exe");
            File.Delete(old);
            File.Move(ExePath, old);
            try { File.Move(file, ExePath); }
            catch { File.Move(old, ExePath); throw; }
            return true;
        }
        catch
        {
            try { File.Delete(file); } catch { }
            try { Process.Start(new ProcessStartInfo(ReleasePage) { UseShellExecute = true }); } catch { }
            return false;
        }
        finally { Report(null); }
    }

    private static async Task DownloadAsync(Release release, string file)
    {
        using var http = NewClient();
        using var response = await http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = File.Create(file);
        var buffer = new byte[81920];
        long done = 0;
        int read, shown = 0;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read));
            done += read;
            int percent = (int)(done * 100 / Math.Max(1, release.Size));
            if (percent != shown) Report(shown = percent);
        }
    }

    private static async Task<string> HashAsync(string file)
    {
        await using var stream = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static void Report(int? percent)
    {
        Progress = percent;
        Changed?.Invoke();
    }
}
