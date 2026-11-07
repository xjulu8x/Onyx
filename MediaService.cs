using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using global::Windows.Media.Control;

namespace DesktopOverlay;

/// <summary>Reads the current media session (Spotify, browsers, ...) via the Windows media transport controls.</summary>
public class MediaService
{
    public static MediaService Instance { get; } = new();

    public event Action Updated;
    /// <summary>Raised when a different track starts.</summary>
    public event Action TrackChanged;

    public bool HasSession { get; private set; }
    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public ImageSource Art { get; private set; }
    public bool IsPlaying { get; private set; }
    /// <summary>Name of the playing app, e.g. "Spotify" or "Chrome".</summary>
    public string Source { get; private set; } = "";

    private static string AppName(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        var name = id.Split('!')[0].Split('\\').Last();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        name = name.Split('.').Last(part => part.Length > 2);
        return name.Length > 0 ? char.ToUpper(name[0]) + name[1..] : "";
    }
    public TimeSpan Duration { get; private set; }
    public TimeSpan Position
    {
        get
        {
            if (!IsPlaying || _posStamp == default) return _pos;
            var p = _pos + (DateTimeOffset.Now - _posStamp);
            return Duration > TimeSpan.Zero && p > Duration ? Duration : p;
        }
    }

    private GlobalSystemMediaTransportControlsSessionManager _mgr;
    private TimeSpan _pos;
    private DateTimeOffset _posStamp;
    private string _trackKey;

    private MediaService()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        t.Tick += async (_, _) => await Poll();
        t.Start();
        _ = Poll();
    }

    private GlobalSystemMediaTransportControlsSession Session => _mgr?.GetCurrentSession();

    private async Task Poll()
    {
        try
        {
            _mgr ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var s = Session;
            if (s == null)
            {
                if (HasSession) { HasSession = false; _trackKey = null; Updated?.Invoke(); }
                return;
            }

            var props = await s.TryGetMediaPropertiesAsync();
            var info = s.GetPlaybackInfo();
            var tl = s.GetTimelineProperties();

            HasSession = true;
            Source = AppName(s.SourceAppUserModelId);
            IsPlaying = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            Duration = tl.EndTime - tl.StartTime;
            _pos = tl.Position;
            _posStamp = tl.LastUpdatedTime;

            var key = props.Title + "|" + props.Artist;
            if (key != _trackKey)
            {
                _trackKey = key;
                Title = props.Title;
                Artist = string.IsNullOrEmpty(props.Artist) ? props.AlbumArtist : props.Artist;
                Art = await LoadThumb(props);
                TrackChanged?.Invoke();
            }
            Updated?.Invoke();
        }
        catch { }
    }

    private static async Task<ImageSource> LoadThumb(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        if (props.Thumbnail == null) return null;
        try
        {
            using var ras = await props.Thumbnail.OpenReadAsync();
            using var src = ras.AsStreamForRead();
            var ms = new MemoryStream();
            await src.CopyToAsync(ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 256; // shown at about 100 px; full-size covers would only cost memory
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    public async void PlayPause() { if (Session is { } s) await s.TryTogglePlayPauseAsync(); await Poll(); }
    public async void Next() { if (Session is { } s) await s.TrySkipNextAsync(); }
    public async void Previous() { if (Session is { } s) await s.TrySkipPreviousAsync(); }

    public static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
