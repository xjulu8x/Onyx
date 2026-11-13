using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DesktopOverlay;

/// <summary>
/// Tells games from other programs, so only a game in full screen makes the island and the dock step aside, while a
/// video in the browser or a full-screen app leaves them alone. Signs of a game, cheapest first:
/// exclusive full screen (Direct3D), a game folder (Steam, Epic, Riot, …), or Windows' own list of detected games (Game Bar).
/// The answer is kept per process, so the check runs once per program.
/// </summary>
internal static class Games
{
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

    private static readonly string[] Folders =
    {
        @"\steamapps\", @"\epic games\", @"\riot games\", @"\battle.net\", @"\blizzard", @"\ea games\", @"\electronic arts\", @"\ubisoft\",
        @"\gog galaxy\games\", @"\gog games\", @"\xboxgames\", @"\origin games\", @"\rockstar games\", @"\minecraft", @"\roblox\", @"\games\",
    };
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "arc", "vlc", "mpc-hc64", "mpc-hc", "potplayer64", "wmplayer", "video.ui",
        "explorer", "applicationframehost", "powerpnt", "obs64", "discord", "spotify", "code", "devenv", "claude",
    };
    private static readonly Dictionary<int, bool> Known = new();
    private static HashSet<string> _gameBar;
    private static DateTime _gameBarRead;

    public static bool IsGame(IntPtr window)
    {
        if (SHQueryUserNotificationState(out var state) == 0 && state == 3) return true; // exclusive Direct3D full screen
        GetWindowThreadProcessId(window, out var pid);
        if (pid == 0) return false;
        if (Known.TryGetValue(pid, out var known)) return known;
        bool game = false;
        // The limited query also works for games protected by anti-cheat.
        string path = TaskbarApps.PathOf((uint)pid)?.ToLowerInvariant();
        if (path != null && !NotGames.Contains(Path.GetFileNameWithoutExtension(path)))
            game = Folders.Any(path.Contains) || GameBarList().Contains(path);
        if (Known.Count > 200) Known.Clear();
        return Known[pid] = game;
    }

    /// <summary>The programs Windows itself recognized as games (Game Bar), from the registry; read again every ten minutes.</summary>
    private static HashSet<string> GameBarList()
    {
        if (_gameBar != null && (DateTime.Now - _gameBarRead).TotalMinutes < 10) return _gameBar;
        _gameBarRead = DateTime.Now;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var children = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
            foreach (var name in children?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var child = children.OpenSubKey(name);
                if (child?.GetValue("MatchedExeFullPath") is string exe) set.Add(exe.ToLowerInvariant());
            }
        }
        catch { }
        return _gameBar = set;
    }
}
